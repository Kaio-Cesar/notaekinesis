import { Elysia } from "elysia";

import { sseResponse } from "../../utils/sse";
import { authGuard } from "../auth";
import type { Auth } from "../auth/service";
import { SyncModel, syncModels } from "./model";
import type { SyncService } from "./service";

/** Sync endpoints used by the native clients. */
export const syncController = ({ auth, sync }: { auth: Auth; sync: SyncService }) =>
  new Elysia({ name: "sync.controller", prefix: "/api/sync", detail: { tags: ["sync"] } })
    .use(syncModels)
    .use(authGuard(auth))
    .guard({ auth: true }, (app) =>
      app
        .post("/push", ({ user, body: { mutations } }) => sync.push(user.id, mutations), {
          body: "PushBody",
          response: "PushResponse",
        })
        .get("/pull", ({ user, query: { since, limit } }) => sync.pull(user.id, since, limit), {
          query: SyncModel.pullQuery,
          response: "PullResponse",
        })
        .get(
          "/events",
          ({ user, request: { signal } }) =>
            sseResponse({
              event: "rev",
              initial: { rev: sync.currentRev(user.id) },
              subscribe: (send) =>
                sync.onChange((userId, rev) => {
                  if (userId === user.id) send({ rev });
                }),
              signal,
            }),
          { detail: { description: "Server-Sent Events: `rev` is sent whenever the user's data changes." } },
        ),
    );
