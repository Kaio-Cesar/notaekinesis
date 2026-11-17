import { treaty } from "@elysiajs/eden";
import type { App, ItemModel } from "@notaekinesis/server";

/** End-to-end typed client for the server API (same origin; cookies carry the session). */
export const api = treaty<App>(window.location.origin, { fetch: { credentials: "include" } }).api;

export type Item = ItemModel["item"];
export type ItemCreate = ItemModel["create"];
export type ItemPatch = ItemModel["patch"];
export type TagCount = ItemModel["tagCounts"][number];
