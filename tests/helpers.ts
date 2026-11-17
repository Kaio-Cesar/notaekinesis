import { mkdtempSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

import { createApp } from "../server/src/app";
import type { Config } from "../server/src/config";
import { openDb } from "../server/src/db";
import { session, user } from "../server/src/db/schema";
import type { ItemModel } from "../server/src/modules/items/model";

type ItemInput = ItemModel["input"];

export const BASE = "http://localhost:3000";

export function testConfig(overrides: Partial<Config> = {}): Config {
  return {
    publicUrl: BASE,
    port: 0,
    dataDir: mkdtempSync(join(tmpdir(), "notaekinesis-test-")),
    authSecret: "test-secret-test-secret-test-secret-0123",
    oidc: null,
    webDist: null,
    maxUploadBytes: 1024 * 1024,
    ...overrides,
  };
}

export function setup(overrides: Partial<Config> = {}) {
  const db = openDb(":memory:");
  const app = createApp({ db, config: testConfig(overrides) });

  /** Creates a user and a session directly in the database, returning its bearer token. */
  function createUser(name = "alice", opts: { expiresAt?: Date } = {}) {
    const userId = crypto.randomUUID();
    const token = crypto.randomUUID().replaceAll("-", "");
    db.insert(user)
      .values({ id: userId, name, email: `${name}-${userId}@example.test` })
      .run();
    db.insert(session)
      .values({
        id: crypto.randomUUID(),
        userId,
        token,
        expiresAt: opts.expiresAt ?? new Date(Date.now() + 86_400_000),
      })
      .run();
    return { userId, token };
  }

  async function request(token: string | null, method: string, path: string, body?: unknown) {
    const headers = new Headers();
    if (token) headers.set("authorization", `Bearer ${token}`);
    if (body !== undefined) headers.set("content-type", "application/json");
    const res = await app.handle(
      new Request(BASE + path, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) }),
    );
    const text = await res.text();
    let json: any = null;
    try {
      json = text ? JSON.parse(text) : null;
    } catch {}
    return { status: res.status, json, text, headers: res.headers };
  }

  /** Uploads bytes as a multipart file, like the web app does. */
  async function upload(token: string | null, bytes: Uint8Array, name: string, type = "application/octet-stream") {
    const form = new FormData();
    form.append("file", new File([bytes], name, { type }));
    const headers = new Headers();
    if (token) headers.set("authorization", `Bearer ${token}`);
    const res = await app.handle(new Request(BASE + "/api/files", { method: "POST", headers, body: form }));
    const text = await res.text();
    let json: any = null;
    try {
      json = JSON.parse(text);
    } catch {}
    return { status: res.status, json, text };
  }

  return { db, app, createUser, request, upload };
}

let clock = 1_700_000_000_000;

export function makeItem(overrides: Partial<ItemInput> = {}): ItemInput {
  clock += 1000;
  return {
    id: Bun.randomUUIDv7(),
    kind: "note",
    text: "hello",
    url: null,
    title: null,
    description: null,
    pinned: false,
    archived: false,
    tags: [],
    createdAt: clock,
    updatedAt: clock,
    deletedAt: null,
    ...overrides,
  };
}

export function mutation(item: ItemInput, mutationId: string = crypto.randomUUID()) {
  return { mutationId, item };
}
