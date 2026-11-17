import { describe, expect, test } from "bun:test";

import { makeItem, mutation, setup } from "./helpers";

describe("sync push/pull", () => {
  test("a pushed item can be pulled by another client", async () => {
    const { createUser, request } = setup();
    const { token } = createUser();
    const item = makeItem({ text: "first note", tags: ["#Read-Later", "read-later", " work "] });

    const push = await request(token, "POST", "/api/sync/push", { mutations: [mutation(item)] });
    expect(push.status).toBe(200);
    expect(push.json.results[0]).toMatchObject({ itemId: item.id, status: "applied", rev: 1 });
    expect(push.json.rev).toBe(1);

    const pull = await request(token, "GET", "/api/sync/pull?since=0");
    expect(pull.status).toBe(200);
    expect(pull.json.nextRev).toBe(1);
    expect(pull.json.hasMore).toBe(false);
    expect(pull.json.items).toHaveLength(1);
    expect(pull.json.items[0]).toMatchObject({ id: item.id, text: "first note", rev: 1, tags: ["read-later", "work"] });
  });

  test("last writer wins on updatedAt; older edits come back as stale", async () => {
    const { createUser, request } = setup();
    const { token } = createUser();
    const base = makeItem({ text: "v1" });
    await request(token, "POST", "/api/sync/push", { mutations: [mutation(base)] });

    // Two clients edited offline. The newer edit arrives first, the older one later.
    const newer = { ...base, text: "from phone", updatedAt: base.updatedAt + 2000 };
    const older = { ...base, text: "from desktop", updatedAt: base.updatedAt + 1000 };
    const r1 = await request(token, "POST", "/api/sync/push", { mutations: [mutation(newer)] });
    const r2 = await request(token, "POST", "/api/sync/push", { mutations: [mutation(older)] });

    expect(r1.json.results[0].status).toBe("applied");
    expect(r2.json.results[0]).toMatchObject({ status: "stale", rev: r1.json.results[0].rev });
    const item = await request(token, "GET", `/api/items/${base.id}`);
    expect(item.json.text).toBe("from phone");
  });

  test("retrying a mutation is idempotent", async () => {
    const { createUser, request } = setup();
    const { token } = createUser();
    const m = mutation(makeItem());

    const first = await request(token, "POST", "/api/sync/push", { mutations: [m] });
    const retry = await request(token, "POST", "/api/sync/push", { mutations: [m] });
    expect(retry.json.results).toEqual(first.json.results);
    expect(retry.json.rev).toBe(1);
  });

  test("deletes propagate as tombstones and leave the feed", async () => {
    const { createUser, request } = setup();
    const { token } = createUser();
    const item = makeItem({ tags: ["x"] });
    await request(token, "POST", "/api/sync/push", { mutations: [mutation(item)] });

    const tomb = { ...item, deletedAt: item.updatedAt + 1, updatedAt: item.updatedAt + 1 };
    await request(token, "POST", "/api/sync/push", { mutations: [mutation(tomb)] });

    const pull = await request(token, "GET", "/api/sync/pull?since=1");
    expect(pull.json.items).toHaveLength(1);
    expect(pull.json.items[0].deletedAt).toBe(tomb.deletedAt);

    const list = await request(token, "GET", "/api/items");
    expect(list.json.items).toHaveLength(0);
    const tags = await request(token, "GET", "/api/tags");
    expect(tags.json).toEqual([]);
  });

  test("pull pages through changes in rev order", async () => {
    const { createUser, request } = setup();
    const { token } = createUser();
    await request(token, "POST", "/api/sync/push", {
      mutations: Array.from({ length: 5 }, () => mutation(makeItem())),
    });

    const seen: number[] = [];
    let since = 0;
    for (;;) {
      // Pages depend on the previous one's cursor, so they must be fetched in order.
      // oxlint-disable-next-line no-await-in-loop
      const page = await request(token, "GET", `/api/sync/pull?since=${since}&limit=2`);
      seen.push(...page.json.items.map((i: { rev: number }) => i.rev));
      since = page.json.nextRev;
      if (!page.json.hasMore) break;
    }
    expect(seen).toEqual([1, 2, 3, 4, 5]);
    expect(since).toBe(5);
  });

  test("users cannot see or overwrite each other's items", async () => {
    const { createUser, request } = setup();
    const alice = createUser("alice");
    const bob = createUser("bob");
    const item = makeItem({ text: "alice's secret" });
    await request(alice.token, "POST", "/api/sync/push", { mutations: [mutation(item)] });

    const hijack = { ...item, text: "bob was here", updatedAt: item.updatedAt + 10_000 };
    const push = await request(bob.token, "POST", "/api/sync/push", { mutations: [mutation(hijack)] });
    expect(push.json.results[0].status).toBe("rejected");

    expect((await request(bob.token, "GET", "/api/sync/pull?since=0")).json.items).toHaveLength(0);
    expect((await request(bob.token, "GET", `/api/items/${item.id}`)).status).toBe(404);
    expect((await request(alice.token, "GET", `/api/items/${item.id}`)).json.text).toBe("alice's secret");
  });

  test("invalid payloads are rejected by validation", async () => {
    const { createUser, request } = setup();
    const { token } = createUser();
    const bad = await request(token, "POST", "/api/sync/push", {
      mutations: [mutation({ ...makeItem(), id: "not-a-uuid" })],
    });
    expect(bad.status).toBe(422);
  });
});

describe("REST endpoints", () => {
  test("create, patch, search, filter by tag and archive", async () => {
    const { createUser, request } = setup();
    const { token } = createUser();

    const link = await request(token, "POST", "/api/items", {
      url: "https://bun.sh/docs",
      text: "Bun documentation",
      tags: ["dev"],
    });
    expect(link.status).toBe(201);
    expect(link.json.kind).toBe("link");
    await request(token, "POST", "/api/items", { text: "Café recipe", tags: ["food", "dev"] });
    await request(token, "POST", "/api/items", { text: "Unrelated" });

    const search = await request(token, "GET", "/api/items?q=docum");
    expect(search.json.items.map((i: { id: string }) => i.id)).toEqual([link.json.id]);
    const accent = await request(token, "GET", "/api/items?q=cafe");
    expect(accent.json.items).toHaveLength(1);

    const both = await request(token, "GET", "/api/items?tags=dev,food");
    expect(both.json.items).toHaveLength(1);

    const patched = await request(token, "PATCH", `/api/items/${link.json.id}`, { pinned: true, archived: true });
    expect(patched.status).toBe(200);
    expect(patched.json.rev).toBeGreaterThan(link.json.rev);

    const feed = await request(token, "GET", "/api/items");
    expect(feed.json.items).toHaveLength(2);
    const archived = await request(token, "GET", "/api/items?archived=true");
    expect(archived.json.items.map((i: { id: string }) => i.id)).toEqual([link.json.id]);

    const tags = await request(token, "GET", "/api/tags");
    expect(tags.json).toEqual([
      { tag: "dev", count: 1 },
      { tag: "food", count: 1 },
    ]);

    expect((await request(token, "DELETE", `/api/items/${link.json.id}`)).status).toBe(204);
    expect((await request(token, "DELETE", `/api/items/${link.json.id}`)).status).toBe(404);
  });

  test("pinned items come first in the feed", async () => {
    const { createUser, request } = setup();
    const { token } = createUser();
    const a = await request(token, "POST", "/api/items", { text: "old pinned", pinned: true });
    await request(token, "POST", "/api/items", { text: "newer" });
    const feed = await request(token, "GET", "/api/items");
    expect(feed.json.items[0].id).toBe(a.json.id);
  });
});

describe("live events", () => {
  test("SSE stream announces new revs", async () => {
    const { app, createUser, request } = setup();
    const { token } = createUser();
    const controller = new AbortController();
    const res = await app.handle(
      new Request("http://localhost:3000/api/sync/events", {
        headers: { authorization: `Bearer ${token}` },
        signal: controller.signal,
      }),
    );
    expect(res.headers.get("content-type")).toContain("text/event-stream");
    const reader = res.body!.getReader();
    const decoder = new TextDecoder();

    const first = decoder.decode((await reader.read()).value);
    expect(first).toContain(`data: {"rev":0}`);

    await request(token, "POST", "/api/sync/push", { mutations: [mutation(makeItem())] });
    const next = decoder.decode((await reader.read()).value);
    expect(next).toContain(`data: {"rev":1}`);
    controller.abort();
    await reader.cancel();
  });
});
