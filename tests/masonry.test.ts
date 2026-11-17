import { describe, expect, test } from "bun:test";

import { estimateHeight, masonry, type Sized } from "../web/src/lib/masonry";

const note = (text: string, extra: Partial<Sized> = {}): Sized & { text: string } => ({
  text,
  url: null,
  description: null,
  tags: [],
  ...extra,
});

describe("masonry layout", () => {
  test("first row keeps reading order left to right", () => {
    const items = ["a", "b", "c", "d"].map((t) => note(t));
    expect(masonry(items, 3).map((col) => col.map((i) => i.text))).toEqual([["a", "d"], ["b"], ["c"]]);
  });

  test("next item goes under the shortest column", () => {
    const long = note("x".repeat(38 * 8));
    const items = [long, note("b"), note("c"), note("d")];
    const [first, second] = masonry(items, 2);
    expect(first!.map((i) => i.text)).toEqual([long.text]);
    expect(second!.map((i) => i.text)).toEqual(["b", "c", "d"]);
  });

  test("one column keeps the original order", () => {
    const items = ["a", "b", "c"].map((t) => note(t));
    expect(masonry(items, 1)).toEqual([items]);
    expect(masonry(items, 0)).toEqual([items]);
  });

  test("long notes are capped like the clamped card", () => {
    expect(estimateHeight(note("line\n".repeat(100)))).toBe(estimateHeight(note("line\n".repeat(12))));
  });
});
