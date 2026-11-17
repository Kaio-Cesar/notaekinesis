import { join } from "node:path";

import { openapi } from "@elysiajs/openapi";
import { Elysia } from "elysia";

import type { Config } from "./config";
import type { Db } from "./db";
import { authController } from "./modules/auth";
import { createAuth } from "./modules/auth/service";
import { filesController } from "./modules/files";
import { FileService } from "./modules/files/service";
import { itemsController } from "./modules/items";
import { ItemService } from "./modules/items/service";
import { syncController } from "./modules/sync";
import { SyncService } from "./modules/sync/service";
import { staticSite } from "./utils/static";

/** Wires services to controllers. Services are created once per app, so tests can use their own database. */
export function createApp({ db, config }: { db: Db; config: Config }) {
  const auth = createAuth(db, config);
  const sync = new SyncService(db);
  const items = new ItemService(db, sync);
  const files = new FileService(db, join(config.dataDir, "files"), config.maxUploadBytes);

  const app = new Elysia()
    .use(
      openapi({
        path: "/api/docs",
        specPath: "/api/openapi.json",
        documentation: { info: { title: "notaekinesis", version: "0.1.0" } },
      }),
    )
    .get("/healthz", () => "ok", { detail: { hide: true } })
    .use(authController({ auth, config }))
    .use(itemsController({ auth, items }))
    .use(syncController({ auth, sync }))
    .use(filesController({ auth, files }));

  // Registered last and outside the typed chain: the SPA fallback isn't part of the API.
  if (config.webDist) app.use(staticSite(config.webDist));
  return app;
}

export type App = ReturnType<typeof createApp>;
export type { AuthModel } from "./modules/auth/model";
export type { FileModel } from "./modules/files/model";
export type { ItemModel } from "./modules/items/model";
export type { SyncModel } from "./modules/sync/model";
