import { Elysia, t, type UnwrapSchema } from "elysia";

const tag = t.String({ minLength: 1, maxLength: 64 });
const tags = t.Array(tag, { maxItems: 50 });
const nullableString = (maxLength: number) => t.Nullable(t.String({ maxLength }));

/** A full item as written by a client. Times are epoch milliseconds. */
const input = t.Object({
  id: t.String({ format: "uuid" }),
  kind: t.UnionEnum(["note", "link"]),
  text: t.String({ maxLength: 100_000 }),
  url: nullableString(4096),
  title: nullableString(1000),
  description: nullableString(4000),
  pinned: t.Boolean(),
  archived: t.Boolean(),
  tags,
  createdAt: t.Integer({ minimum: 0 }),
  updatedAt: t.Integer({ minimum: 0 }),
  deletedAt: t.Nullable(t.Integer({ minimum: 0 })),
});

const item = t.Object({
  ...input.properties,
  siteName: t.Nullable(t.String()),
  faviconUrl: t.Nullable(t.String()),
  rev: t.Integer(),
});

export const ItemModel = {
  input,
  item,
  params: t.Object({ id: t.String() }),
  create: t.Object({
    id: t.Optional(t.String({ format: "uuid" })),
    text: t.Optional(t.String({ maxLength: 100_000 })),
    url: t.Optional(nullableString(4096)),
    title: t.Optional(nullableString(1000)),
    pinned: t.Optional(t.Boolean()),
    tags: t.Optional(tags),
  }),
  patch: t.Partial(
    t.Object({
      text: t.String({ maxLength: 100_000 }),
      url: nullableString(4096),
      title: nullableString(1000),
      description: nullableString(4000),
      pinned: t.Boolean(),
      archived: t.Boolean(),
      tags,
    }),
  ),
  listQuery: t.Object({
    q: t.Optional(t.String({ maxLength: 500 })),
    /** Comma-separated; items must have all of them. */
    tags: t.Optional(t.String({ maxLength: 1000 })),
    // Don't use an optional UnionEnum here: Elysia fills it with the first member.
    archived: t.Optional(t.Boolean()),
    pinned: t.Optional(t.Boolean()),
    limit: t.Integer({ minimum: 1, maximum: 200, default: 50 }),
    offset: t.Integer({ minimum: 0, default: 0 }),
  }),
  list: t.Object({ items: t.Array(item), hasMore: t.Boolean() }),
  tagCounts: t.Array(t.Object({ tag: t.String(), count: t.Integer() })),
  notFound: t.Literal("Not Found"),
  conflict: t.Literal("Conflict"),
} as const;

export type ItemModel = { [K in keyof typeof ItemModel]: UnwrapSchema<(typeof ItemModel)[K]> };

/** Named models, so they show up as reusable schemas in the OpenAPI document. */
export const itemModels = new Elysia({ name: "items.model" }).model({
  Item: ItemModel.item,
  ItemInput: ItemModel.input,
  ItemCreate: ItemModel.create,
  ItemPatch: ItemModel.patch,
  ItemList: ItemModel.list,
  TagCounts: ItemModel.tagCounts,
});
