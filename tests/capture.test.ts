import { describe, expect, test } from "bun:test";

import { parseCapture, toCaptureText } from "../web/src/lib/capture";

describe("quick-add parsing", () => {
  test("a bare URL becomes a link with no text", () => {
    expect(parseCapture("  https://bun.sh/docs  ")).toEqual({
      text: "",
      url: "https://bun.sh/docs",
      title: null,
      tags: [],
    });
  });

  test("plain text stays a note", () => {
    expect(parseCapture("buy milk\nand eggs")).toEqual({
      text: "buy milk\nand eggs",
      url: null,
      title: null,
      tags: [],
    });
  });

  test("tags are pulled out and lower-cased; text around them is kept", () => {
    expect(parseCapture("Read this later #Reading #dev #reading")).toEqual({
      text: "Read this later",
      url: null,
      title: null,
      tags: ["reading", "dev"],
    });
  });

  test("a URL with a comment becomes a link with text", () => {
    expect(parseCapture("great talk https://youtu.be/abc123?t=42 #video")).toEqual({
      text: "great talk",
      url: "https://youtu.be/abc123?t=42",
      title: null,
      tags: ["video"],
    });
  });

  test("trailing punctuation is not part of the URL, and URL fragments are not tags", () => {
    expect(parseCapture("see https://example.com/page#section.")?.url).toBe("https://example.com/page#section");
    expect(parseCapture("see https://example.com/page#section.")?.tags).toEqual([]);
  });

  test("a URL alone on the first line becomes the link, with a Markdown body below", () => {
    expect(parseCapture("https://bun.sh\n\n## Why\n- fast\n- *fun*\n\n#dev")).toEqual({
      text: "## Why\n- fast\n- *fun*",
      url: "https://bun.sh",
      title: null,
      tags: ["dev"],
    });
  });

  test("Markdown structure survives: indentation, headings and code", () => {
    const note =
      "# Plan\n\n- step one\n    - nested  step\n\n```c\n#include <stdio.h>\nint  x;\n```\n\nUse `#define` wisely #c";
    expect(parseCapture(note)).toEqual({
      text: "# Plan\n\n- step one\n    - nested  step\n\n```c\n#include <stdio.h>\nint  x;\n```\n\nUse `#define` wisely",
      url: null,
      title: null,
      tags: ["c"],
    });
  });

  test("URLs in Markdown links, code, or longer notes stay in the text", () => {
    expect(parseCapture("see [the docs](https://bun.sh/docs) #dev")).toEqual({
      text: "see [the docs](https://bun.sh/docs)",
      url: null,
      title: null,
      tags: ["dev"],
    });
    expect(parseCapture("run `curl https://x.dev/install`")?.url).toBeNull();
    expect(parseCapture("Links:\n- https://a.dev\n- https://b.dev")?.url).toBeNull();
    expect(parseCapture("compare https://a.dev and https://b.dev")?.url).toBeNull();
  });

  test("editing round-trips through toCaptureText", () => {
    const item = { url: "https://bun.sh", text: "## Notes\n- one\n  - two", tags: ["dev", "reading"] };
    expect(parseCapture(toCaptureText(item))).toEqual({ ...item, title: null });
    const note = { url: null, text: "plain *note*", tags: [] };
    expect(parseCapture(toCaptureText(note))).toEqual({ ...note, title: null });
  });

  test("empty input and tag-only input give nothing to save", () => {
    expect(parseCapture("   ")).toBeNull();
    expect(parseCapture("#just-a-tag")).toBeNull();
  });
});
