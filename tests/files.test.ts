import { describe, expect, test } from "bun:test";

import { cleanFileName, parseRange, sniffMime } from "../server/src/modules/files/service";
import { BASE, makeItem, mutation, setup } from "./helpers";

const bytes = (...parts: (string | number[])[]) =>
  new Uint8Array(parts.flatMap((p) => (typeof p === "string" ? Array.from(new TextEncoder().encode(p)) : p)));

// Minimal headers are enough: the server only looks at the first bytes.
const PNG = bytes([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a], "rest of a png".repeat(10));
const PDF = bytes("%PDF-1.7\n", "x".repeat(200));
const MP4 = bytes([0, 0, 0, 0x18], "ftypisom", "y".repeat(2000));

describe("file type detection", () => {
  test("recognizes images, videos and PDFs by content", () => {
    expect(sniffMime(PNG)).toBe("image/png");
    expect(sniffMime(bytes([0xff, 0xd8, 0xff, 0xe0]))).toBe("image/jpeg");
    expect(sniffMime(bytes("GIF89a"))).toBe("image/gif");
    expect(sniffMime(bytes("RIFF", [0, 0, 0, 0], "WEBP"))).toBe("image/webp");
    expect(sniffMime(bytes([0, 0, 0, 0x1c], "ftypavif"))).toBe("image/avif");
    expect(sniffMime(MP4)).toBe("video/mp4");
    expect(sniffMime(bytes([0, 0, 0, 0x14], "ftypqt  "))).toBe("video/quicktime");
    expect(sniffMime(bytes([0x1a, 0x45, 0xdf, 0xa3]))).toBe("video/webm");
    expect(sniffMime(PDF)).toBe("application/pdf");
  });

  test("refuses anything that could run script on this origin, and unknown types", () => {
    expect(sniffMime(bytes("<svg xmlns='http://www.w3.org/2000/svg'><script>alert(1)</script></svg>"))).toBeNull();
    expect(sniffMime(bytes("<!doctype html><script>alert(1)</script>"))).toBeNull();
    expect(sniffMime(bytes("plain text"))).toBeNull();
    expect(sniffMime(bytes([0, 0, 0, 0x18], "ftypheic"))).toBeNull();
  });

  test("file names are cleaned up", () => {
    expect(cleanFileName("C:\\Users\\me\\shot.png")).toBe("shot.png");
    expect(cleanFileName('../../etc/"passwd"\n')).toBe("passwd");
    expect(cleanFileName("")).toBe("file");
  });

  test("range headers", () => {
    expect(parseRange(null, 100)).toBeNull();
    expect(parseRange("bytes=0-9", 100)).toEqual({ start: 0, end: 9 });
    expect(parseRange("bytes=90-", 100)).toEqual({ start: 90, end: 99 });
    expect(parseRange("bytes=-10", 100)).toEqual({ start: 90, end: 99 });
    expect(parseRange("bytes=50-1000", 100)).toEqual({ start: 50, end: 99 });
    expect(parseRange("bytes=100-", 100)).toBe("invalid");
    expect(parseRange("bytes=0-1,5-6", 100)).toBe("invalid");
  });
});

describe("uploads", () => {
  test("upload, attach to an item, and download", async () => {
    const { createUser, request, upload, app } = setup();
    const { token } = createUser();

    const up = await upload(token, PNG, "Screenshot 2026.png", "text/html");
    expect(up.status).toBe(201);
    // The detected type wins over what the client claimed.
    expect(up.json).toMatchObject({ name: "Screenshot 2026.png", mime: "image/png", size: PNG.length });

    const created = await request(token, "POST", "/api/items", {
      fileId: up.json.id,
      text: "the *bug*",
      tags: ["bug"],
    });
    expect(created.status).toBe(201);
    expect(created.json).toMatchObject({
      kind: "file",
      text: "the *bug*",
      file: { id: up.json.id, name: "Screenshot 2026.png", mime: "image/png", size: PNG.length },
    });

    const res = await app.handle(
      new Request(`${BASE}/api/files/${up.json.id}`, { headers: { authorization: `Bearer ${token}` } }),
    );
    expect(res.status).toBe(200);
    expect(res.headers.get("content-type")).toBe("image/png");
    expect(res.headers.get("x-content-type-options")).toBe("nosniff");
    expect(res.headers.get("content-disposition")).toContain("Screenshot%202026.png");
    expect(new Uint8Array(await res.arrayBuffer())).toEqual(PNG);

    // Searchable by file name.
    const search = await request(token, "GET", "/api/items?q=screenshot");
    expect(search.json.items).toHaveLength(1);
  });

  test("videos can be fetched in ranges", async () => {
    const { createUser, upload, app } = setup();
    const { token } = createUser();
    const { json } = await upload(token, MP4, "clip.mp4");
    const get = (range: string) =>
      app.handle(new Request(`${BASE}/api/files/${json.id}`, { headers: { authorization: `Bearer ${token}`, range } }));

    const part = await get("bytes=4-11");
    expect(part.status).toBe(206);
    expect(part.headers.get("content-range")).toBe(`bytes 4-11/${MP4.length}`);
    expect(await part.text()).toBe("ftypisom");

    const bad = await get(`bytes=${MP4.length}-`);
    expect(bad.status).toBe(416);
  });

  test("rejects unsupported, empty and oversized files", async () => {
    const { createUser, upload } = setup({ maxUploadBytes: 1000 });
    const { token } = createUser();
    expect((await upload(token, bytes("<svg/>"), "x.svg", "image/svg+xml")).status).toBe(415);
    expect((await upload(token, new Uint8Array(), "empty.png")).status).toBe(400);
    expect((await upload(token, MP4, "big.mp4")).status).toBe(413);
    expect((await upload(null, PNG, "a.png")).status).toBe(401);
  });

  test("files are private to their owner", async () => {
    const { createUser, request, upload, app } = setup();
    const alice = createUser("alice");
    const bob = createUser("bob");
    const { json: file } = await upload(alice.token, PDF, "taxes.pdf");

    const res = await app.handle(
      new Request(`${BASE}/api/files/${file.id}`, { headers: { authorization: `Bearer ${bob.token}` } }),
    );
    expect(res.status).toBe(404);

    // Bob can't attach Alice's file either, through REST or sync.
    expect((await request(bob.token, "POST", "/api/items", { fileId: file.id })).status).toBe(400);
    const push = await request(bob.token, "POST", "/api/sync/push", {
      mutations: [mutation(makeItem({ fileId: file.id }))],
    });
    expect(push.json.results[0].status).toBe("rejected");
  });

  test("sync keeps the file when a client leaves fileId out, and kind follows content", async () => {
    const { createUser, request, upload } = setup();
    const { token } = createUser();
    const { json: file } = await upload(token, PDF, "manual.pdf");
    const item = makeItem({ fileId: file.id, kind: "note", text: "printer manual" });
    await request(token, "POST", "/api/sync/push", { mutations: [mutation(item)] });

    // An older client that doesn't know about files edits the text.
    const { fileId: _, ...withoutFile } = item;
    await request(token, "POST", "/api/sync/push", {
      mutations: [mutation({ ...withoutFile, text: "printer manual (office)", updatedAt: item.updatedAt + 1 })],
    });

    const pulled = (await request(token, "GET", "/api/sync/pull?since=0")).json.items[0];
    expect(pulled).toMatchObject({ kind: "file", text: "printer manual (office)", file: { name: "manual.pdf" } });

    // Editing through REST keeps the file too.
    const patched = await request(token, "PATCH", `/api/items/${item.id}`, { text: "renamed", url: null });
    expect(patched.json).toMatchObject({ kind: "file", file: { id: file.id } });
  });
});
