import { openapi } from "@elysiajs/openapi";
import { Elysia } from "elysia";

import type { Config } from "./config";
import type { Db } from "./db";
import { authController } from "./modules/auth";
import { createAuth } from "./modules/auth/service";
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
    .use(syncController({ auth, sync }));

  return config.webDist ? app.use(staticSite(config.webDist)) : app;
}

export type App = ReturnType<typeof createApp>;
