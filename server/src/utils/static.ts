import { join, normalize } from "node:path";

import { Elysia } from "elysia";

/**
 * Serves a built single-page app, falling back to index.html for client-side routes.
 *
 * It answers only requests no route matched. A `GET /*` route would outrank mounted
 * handlers (better-auth's `/api/auth/*` is registered for all methods), so GETs such as
 * the OIDC callback would land here instead.
 */
export const staticSite = (dir: string) => {
  const root = normalize(dir);
  return new Elysia({ name: "static", seed: root }).onError({ as: "global" }, async ({ code, request }) => {
    if (code !== "NOT_FOUND" || (request.method !== "GET" && request.method !== "HEAD")) return;
    let rel: string;
    try {
      rel = normalize(decodeURIComponent(new URL(request.url).pathname)).replace(/^[/\\]+/, "");
    } catch {
      return; // malformed escapes
    }
    // Unknown API paths should 404 instead of returning the app shell.
    if (rel === "api" || rel.startsWith("api/") || rel.startsWith("api\\")) return;
    const path = join(root, rel);
    if (rel && path.startsWith(root)) {
      const file = Bun.file(path);
      if (await file.exists()) return new Response(file);
    }
    return new Response(Bun.file(join(root, "index.html")), {
      headers: { "content-type": "text/html; charset=utf-8" },
    });
  });
};
