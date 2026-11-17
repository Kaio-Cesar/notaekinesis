import { and, asc, eq, gt, sql } from "drizzle-orm";

import type { Db } from "../../db";
import { appliedMutations, files, itemTags, items, userSync } from "../../db/schema";
import type { ItemModel } from "../items/model";
import { kindOf, normalizeTags, toItem } from "../items/service";
import type { SyncModel } from "./model";

type Tx = Parameters<Parameters<Db["transaction"]>[0]>[0];

export type ChangeListener = (userId: string, rev: number) => void;

/**
 * Offline-first sync: clients push mutations (last-writer-wins on `updatedAt`)
 * and pull everything changed after a per-user revision number.
 * Not tied to Elysia; the controller passes plain values in.
 */
export class SyncService {
  private listeners = new Set<ChangeListener>();

  constructor(private readonly db: Db) {}

  /** Subscribe to revision changes. Returns an unsubscribe function. */
  onChange(listener: ChangeListener): () => void {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  currentRev(userId: string): number {
    return this.db.select({ rev: userSync.rev }).from(userSync).where(eq(userSync.userId, userId)).get()?.rev ?? 0;
  }

  push(userId: string, mutations: SyncModel["mutation"][]): SyncModel["pushResponse"] {
    const before = this.currentRev(userId);
    const results = this.db.transaction((tx) => mutations.map((m) => this.applyOne(tx, userId, m.mutationId, m.item)));
    const rev = this.currentRev(userId);
    if (rev !== before) for (const listener of this.listeners) listener(userId, rev);
    return { results, rev };
  }

  pull(userId: string, since: number, limit: number): SyncModel["pullResponse"] {
    const rows = this.db
      .select()
      .from(items)
      .where(and(eq(items.userId, userId), gt(items.rev, since)))
      .orderBy(asc(items.rev))
      .limit(limit + 1)
      .all();
    const hasMore = rows.length > limit;
    const page = hasMore ? rows.slice(0, limit) : rows;
    const nextRev = page.length > 0 ? page[page.length - 1]!.rev : Math.max(since, this.currentRev(userId));
    return { items: page.map(toItem), nextRev, hasMore };
  }

  private nextRev(tx: Tx, userId: string): number {
    return tx
      .insert(userSync)
      .values({ userId, rev: 1 })
      .onConflictDoUpdate({ target: userSync.userId, set: { rev: sql`${userSync.rev} + 1` } })
      .returning({ rev: userSync.rev })
      .get().rev;
  }

  /** Retrying the same `mutationId` returns the original result without writing again. */
  private applyOne(tx: Tx, userId: string, mutationId: string, input: ItemModel["input"]): SyncModel["mutationResult"] {
    const previous = tx
      .select()
      .from(appliedMutations)
      .where(and(eq(appliedMutations.userId, userId), eq(appliedMutations.mutationId, mutationId)))
      .get();
    if (previous) return { mutationId, itemId: previous.itemId, status: previous.status, rev: previous.rev };

    const existing = tx.select().from(items).where(eq(items.id, input.id)).get();
    if (existing && existing.userId !== userId) {
      // The id belongs to someone else. Not recorded: there is nothing to remember.
      return { mutationId, itemId: input.id, status: "rejected", rev: 0 };
    }

    // Leaving `fileId` out keeps the current file, for clients that don't know about files.
    const fileId = input.fileId === undefined ? (existing?.fileId ?? null) : input.fileId;
    const file = fileId
      ? tx
          .select()
          .from(files)
          .where(and(eq(files.id, fileId), eq(files.userId, userId)))
          .get()
      : undefined;
    if (fileId && !file) {
      // Unknown file, or someone else's. Not recorded, so a retry after uploading can still succeed.
      return { mutationId, itemId: input.id, status: "rejected", rev: 0 };
    }

    let status: "applied" | "stale";
    let rev: number;
    if (existing && existing.updatedAt >= input.updatedAt) {
      status = "stale";
      rev = existing.rev;
    } else {
      status = "applied";
      rev = this.nextRev(tx, userId);
      const tags = normalizeTags(input.tags);
      const sameUrl = existing && existing.url === input.url;
      const values = {
        kind: kindOf({ fileId, url: input.url }),
        text: input.text,
        url: input.url,
        fileId,
        fileName: file?.name ?? null,
        fileMime: file?.mime ?? null,
        fileSize: file?.size ?? null,
        // Keep server-fetched preview data unless the client set it or the URL changed.
        title: input.title ?? (sameUrl ? existing.title : null),
        description: input.description ?? (sameUrl ? existing.description : null),
        siteName: sameUrl ? existing.siteName : null,
        faviconUrl: sameUrl ? existing.faviconUrl : null,
        pinned: input.pinned,
        archived: input.archived,
        tags,
        createdAt: existing?.createdAt ?? input.createdAt,
        updatedAt: input.updatedAt,
        deletedAt: input.deletedAt,
        rev,
      };
      tx.insert(items)
        .values({ id: input.id, userId, ...values })
        .onConflictDoUpdate({ target: items.id, set: values })
        .run();

      tx.delete(itemTags).where(eq(itemTags.itemId, input.id)).run();
      if (tags.length > 0 && input.deletedAt === null) {
        tx.insert(itemTags)
          .values(tags.map((tag) => ({ itemId: input.id, userId, tag })))
          .run();
      }
    }

    tx.insert(appliedMutations)
      .values({ mutationId, userId, itemId: input.id, status, rev, appliedAt: Date.now() })
      .run();
    return { mutationId, itemId: input.id, status, rev };
  }
}
