import { join, normalize } from "node:path";

import { Elysia } from "elysia";

/** Serves a built single-page app, falling back to index.html for client-side routes. */
export const staticSite = (dir: string) => {
  const root = normalize(dir);
  return new Elysia({ name: "static", seed: root }).get(
    "/*",
    async ({ params, status }) => {
      const rel = normalize(params["*"] ?? "").replace(/^[/\\]+/, "");
      const path = join(root, rel);
      if (rel && path.startsWith(root)) {
        const file = Bun.file(path);
        if (await file.exists()) return file;
      }
      // Unknown API paths should 404 instead of returning the app shell.
      if (rel.startsWith("api")) return status(404, "Not Found");
      return Bun.file(join(root, "index.html"));
    },
    { detail: { hide: true } },
  );
};
