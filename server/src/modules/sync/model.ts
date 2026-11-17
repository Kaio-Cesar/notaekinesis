import { Elysia, t, type UnwrapSchema } from "elysia";

import { ItemModel } from "../items/model";

const mutation = t.Object({
  mutationId: t.String({ minLength: 1, maxLength: 64 }),
  item: ItemModel.input,
});

const mutationResult = t.Object({
  mutationId: t.String(),
  itemId: t.String(),
  /** applied: written. stale: a newer version already existed. rejected: not allowed (e.g. id owned by another user). */
  status: t.UnionEnum(["applied", "stale", "rejected"]),
  rev: t.Integer(),
});

export const SyncModel = {
  mutation,
  mutationResult,
  pushBody: t.Object({ mutations: t.Array(mutation, { maxItems: 500 }) }),
  pushResponse: t.Object({ results: t.Array(mutationResult), rev: t.Integer() }),
  pullQuery: t.Object({
    since: t.Integer({ minimum: 0, default: 0 }),
    limit: t.Integer({ minimum: 1, maximum: 1000, default: 500 }),
  }),
  pullResponse: t.Object({ items: t.Array(ItemModel.item), nextRev: t.Integer(), hasMore: t.Boolean() }),
} as const;

export type SyncModel = { [K in keyof typeof SyncModel]: UnwrapSchema<(typeof SyncModel)[K]> };

export const syncModels = new Elysia({ name: "sync.model" }).model({
  Mutation: SyncModel.mutation,
  MutationResult: SyncModel.mutationResult,
  PushBody: SyncModel.pushBody,
  PushResponse: SyncModel.pushResponse,
  PullResponse: SyncModel.pullResponse,
});
