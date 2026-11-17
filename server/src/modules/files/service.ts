import { mkdir } from "node:fs/promises";
import { join } from "node:path";

import { and, eq } from "drizzle-orm";
import { status } from "elysia";

import type { Db } from "../../db";
import { files, type FileRow } from "../../db/schema";
import type { FileModel } from "./model";

const ascii = (bytes: Uint8Array, from: number, to: number) => String.fromCharCode(...bytes.subarray(from, to));
const startsWith = (bytes: Uint8Array, sig: number[]) => sig.every((b, i) => bytes[i] === b);

/**
 * Works out the file type from its first bytes. Only images, videos and PDFs are
 * accepted; everything else (including SVG and HTML, which could run scripts on
 * this origin) is refused. The type the client claims is never trusted.
 */
export function sniffMime(bytes: Uint8Array): string | null {
  if (startsWith(bytes, [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a])) return "image/png";
  if (startsWith(bytes, [0xff, 0xd8, 0xff])) return "image/jpeg";
  if (ascii(bytes, 0, 6) === "GIF87a" || ascii(bytes, 0, 6) === "GIF89a") return "image/gif";
  if (ascii(bytes, 0, 4) === "RIFF" && ascii(bytes, 8, 12) === "WEBP") return "image/webp";
  if (ascii(bytes, 0, 5) === "%PDF-") return "application/pdf";
  if (startsWith(bytes, [0x1a, 0x45, 0xdf, 0xa3])) return "video/webm"; // Matroska/WebM
  if (ascii(bytes, 4, 8) === "ftyp") {
    const brand = ascii(bytes, 8, 12);
    if (brand === "avif" || brand === "avis") return "image/avif";
    if (brand === "qt  ") return "video/quicktime";
    if (["heic", "heix", "heim", "heis", "mif1", "msf1"].includes(brand)) return null; // browsers can't show HEIC
    return "video/mp4"; // isom, mp41, mp42, avc1, M4V, 3gp…
  }
  return null;
}

/** Keeps a display name safe for headers and file pickers. */
export function cleanFileName(name: string): string {
  const base = name.split(/[\\/]/).pop() ?? "";
  // oxlint-disable-next-line no-control-regex -- stripping control characters is the point
  const cleaned = base.replace(/[\u0000-\u001f\u007f"]/g, "").trim();
  return (cleaned || "file").slice(0, 255);
}

/** Parses a single `Range: bytes=…` header. Returns null for "whole file", or "invalid". */
export function parseRange(header: string | null, size: number): { start: number; end: number } | null | "invalid" {
  if (!header) return null;
  const m = /^bytes=(\d*)-(\d*)$/.exec(header.trim());
  if (!m || (m[1] === "" && m[2] === "")) return "invalid";
  let start: number;
  let end: number;
  if (m[1] === "") {
    // suffix range: the last N bytes
    start = Math.max(0, size - Number(m[2]));
    end = size - 1;
  } else {
    start = Number(m[1]);
    end = m[2] === "" ? size - 1 : Math.min(Number(m[2]), size - 1);
  }
  if (start > end || start >= size) return "invalid";
  return { start, end };
}

export class FileService {
  constructor(
    private readonly db: Db,
    private readonly dir: string,
    private readonly maxBytes: number,
  ) {}

  private pathOf(id: string) {
    // Spread files over 256 folders by the random end of the UUID.
    return join(this.dir, id.slice(-2), id);
  }

  async save(userId: string, file: File): Promise<FileModel["fileInfo"]> {
    if (file.size === 0) throw status(400, "The file is empty");
    if (file.size > this.maxBytes) {
      throw status(413, `Files can be at most ${Math.floor(this.maxBytes / 1024 / 1024)} MB`);
    }
    const mime = sniffMime(new Uint8Array(await file.slice(0, 32).arrayBuffer()));
    if (!mime)
      throw status(415, "Only images (PNG, JPEG, GIF, WebP, AVIF), videos (MP4, WebM, MOV) and PDFs are supported");

    const id = Bun.randomUUIDv7();
    const path = this.pathOf(id);
    await mkdir(join(path, ".."), { recursive: true });
    await Bun.write(path, file);

    const row = { id, userId, name: cleanFileName(file.name), mime, size: file.size, createdAt: Date.now() };
    this.db.insert(files).values(row).run();
    return { id, name: row.name, mime, size: row.size };
  }

  /** A file the user owns, or null. */
  find(userId: string, id: string): FileRow | null {
    return (
      this.db
        .select()
        .from(files)
        .where(and(eq(files.id, id), eq(files.userId, userId)))
        .get() ?? null
    );
  }

  /** Streams a file, honoring `Range` so videos can seek. */
  async serve(userId: string, id: string, rangeHeader: string | null): Promise<Response> {
    const row = this.find(userId, id);
    const blob = row && Bun.file(this.pathOf(row.id));
    if (!row || !blob || !(await blob.exists())) throw status(404, "Not Found");

    const headers = new Headers({
      "content-type": row.mime,
      "content-disposition": `inline; filename*=UTF-8''${encodeURIComponent(row.name)}`,
      "accept-ranges": "bytes",
      // Files never change, so the browser may keep them for good.
      "cache-control": "private, max-age=31536000, immutable",
      etag: `"${row.id}"`,
      "x-content-type-options": "nosniff",
    });

    const range = parseRange(rangeHeader, row.size);
    if (range === "invalid") {
      headers.set("content-range", `bytes */${row.size}`);
      return new Response(null, { status: 416, headers });
    }
    if (range) {
      headers.set("content-range", `bytes ${range.start}-${range.end}/${row.size}`);
      headers.set("content-length", String(range.end - range.start + 1));
      return new Response(blob.slice(range.start, range.end + 1), { status: 206, headers });
    }
    headers.set("content-length", String(row.size));
    return new Response(blob, { headers });
  }
}
