import { and, asc, desc, eq, sql, type SQL } from "drizzle-orm";
import { status } from "elysia";

import type { Db } from "../../db";
import { itemTags, items, type ItemRow } from "../../db/schema";
import type { SyncService } from "../sync/service";
import type { ItemModel } from "./model";

type Item = ItemModel["item"];

/** Lower-cases, trims, strips a leading `#`, and de-duplicates tags. */
export function normalizeTags(tags: string[]): string[] {
  const out = new Set<string>();
  for (const raw of tags) {
    const tag = raw.trim().replace(/^#+/, "").trim().toLowerCase();
    if (tag) out.add(tag);
  }
  return [...out];
}

export function toItem(row: ItemRow): Item {
  const { userId: _, ...item } = row;
  return item;
}

/** Turns free text into an FTS5 prefix query, or null if there is nothing to search for. */
export function toFtsQuery(q: string): string | null {
  const tokens = q.match(/[\p{L}\p{N}]+/gu);
  if (!tokens) return null;
  return tokens.map((tok) => `"${tok}"*`).join(" ");
}

export interface ListOptions {
  q?: string;
  tags?: string[];
  archived?: boolean;
  pinned?: boolean;
  limit: number;
  offset: number;
}

/**
 * Item queries and server-side edits for the web app. Writes go through the
 * sync service, so the native clients see them like any other change.
 */
export class ItemService {
  constructor(
    private readonly db: Db,
    private readonly sync: SyncService,
  ) {}

  find(userId: string, id: string): Item | null {
    const row = this.db
      .select()
      .from(items)
      .where(and(eq(items.id, id), eq(items.userId, userId)))
      .get();
    return row ? toItem(row) : null;
  }

  /** Like `find`, but throws 404 for missing or deleted items. */
  get(userId: string, id: string): Item {
    const item = this.find(userId, id);
    if (!item || item.deletedAt !== null) throw status(404, "Not Found");
    return item;
  }

  list(userId: string, opts: ListOptions): ItemModel["list"] {
    const where: SQL[] = [eq(items.userId, userId), sql`${items.deletedAt} IS NULL`];
    where.push(eq(items.archived, opts.archived ?? false));
    if (opts.pinned !== undefined) where.push(eq(items.pinned, opts.pinned));
    for (const tag of normalizeTags(opts.tags ?? [])) {
      where.push(
        sql`EXISTS (SELECT 1 FROM ${itemTags} WHERE ${itemTags.itemId} = ${items.id} AND ${itemTags.tag} = ${tag})`,
      );
    }
    const fts = opts.q ? toFtsQuery(opts.q) : null;
    if (fts) where.push(sql`${items.id} IN (SELECT item_id FROM items_fts WHERE items_fts MATCH ${fts})`);

    const rows = this.db
      .select()
      .from(items)
      .where(and(...where))
      .orderBy(desc(items.pinned), desc(items.createdAt), desc(items.id))
      .limit(opts.limit + 1)
      .offset(opts.offset)
      .all();
    const hasMore = rows.length > opts.limit;
    return { items: (hasMore ? rows.slice(0, opts.limit) : rows).map(toItem), hasMore };
  }

  tags(userId: string): ItemModel["tagCounts"] {
    return this.db
      .select({ tag: itemTags.tag, count: sql<number>`count(*)` })
      .from(itemTags)
      .innerJoin(items, eq(items.id, itemTags.itemId))
      .where(and(eq(itemTags.userId, userId), sql`${items.deletedAt} IS NULL`, eq(items.archived, false)))
      .groupBy(itemTags.tag)
      .orderBy(desc(sql`count(*)`), asc(itemTags.tag))
      .all();
  }

  create(userId: string, body: ItemModel["create"]): Item {
    const url = body.url?.trim() || null;
    return this.write(
      userId,
      {
        id: body.id ?? Bun.randomUUIDv7(),
        kind: url ? "link" : "note",
        text: body.text ?? "",
        url,
        title: body.title ?? null,
        description: null,
        pinned: body.pinned ?? false,
        archived: false,
        tags: body.tags ?? [],
        createdAt: Date.now(),
        deletedAt: null,
      },
      null,
    );
  }

  update(userId: string, id: string, patch: ItemModel["patch"]): Item {
    const existing = this.get(userId, id);
    const url = patch.url === undefined ? existing.url : patch.url?.trim() || null;
    return this.write(userId, { ...existing, ...patch, url, kind: url ? "link" : "note" }, existing);
  }

  remove(userId: string, id: string): void {
    const existing = this.get(userId, id);
    this.write(userId, { ...existing, deletedAt: Date.now() }, existing);
  }

  /** Server-side edits always win over the stored version. */
  private write(userId: string, input: Omit<ItemModel["input"], "updatedAt">, existing: Item | null): Item {
    const updatedAt = Math.max(Date.now(), (existing?.updatedAt ?? 0) + 1);
    const item = { ...input, updatedAt };
    const { results } = this.sync.push(userId, [{ mutationId: crypto.randomUUID(), item }]);
    if (results[0]?.status === "rejected") throw status(409, "Conflict");
    return this.find(userId, input.id)!;
  }
}
