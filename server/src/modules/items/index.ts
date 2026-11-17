import { Elysia } from "elysia";

import { authGuard } from "../auth";
import type { Auth } from "../auth/service";
import { ItemModel, itemModels } from "./model";
import type { ItemService } from "./service";

/** REST endpoints used by the web app. */
export const itemsController = ({ auth, items }: { auth: Auth; items: ItemService }) =>
  new Elysia({ name: "items.controller", prefix: "/api", detail: { tags: ["items"] } })
    .use(itemModels)
    .use(authGuard(auth))
    .guard({ auth: true }, (app) =>
      app
        .get(
          "/items",
          ({ user, query: { tags, ...query } }) => items.list(user.id, { ...query, tags: tags?.split(",") }),
          { query: ItemModel.listQuery, response: "ItemList" },
        )
        .post("/items", ({ user, body, status }) => status(201, items.create(user.id, body)), {
          body: "ItemCreate",
          response: { 201: "Item", 409: ItemModel.conflict },
        })
        .get("/items/:id", ({ user, params: { id } }) => items.get(user.id, id), {
          params: ItemModel.params,
          response: { 200: "Item", 404: ItemModel.notFound },
        })
        .patch("/items/:id", ({ user, params: { id }, body }) => items.update(user.id, id, body), {
          params: ItemModel.params,
          body: "ItemPatch",
          response: { 200: "Item", 404: ItemModel.notFound },
        })
        .delete(
          "/items/:id",
          ({ user, params: { id }, status }) => {
            items.remove(user.id, id);
            return status(204);
          },
          { params: ItemModel.params },
        )
        .get("/tags", ({ user }) => items.tags(user.id), { response: "TagCounts" }),
    );
