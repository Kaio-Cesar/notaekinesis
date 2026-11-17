import { describe, expect, test } from "bun:test";

import { renderMarkdown } from "../web/src/lib/markdown";

describe("note Markdown rendering", () => {
  test("renders common Markdown", () => {
    const html = renderMarkdown("## Title\n- **bold** and `code`\nline two");
    expect(html).toContain("<h2>Title</h2>");
    expect(html).toContain("<strong>bold</strong>");
    expect(html).toContain("<code>code</code>");
    expect(html).toContain("<br>");
  });

  test("raw HTML is escaped, not rendered", () => {
    const html = renderMarkdown('<script>alert(1)</script><img src=x onerror="alert(1)">');
    expect(html).not.toContain("<script");
    expect(html).not.toContain("<img");
    expect(html).toContain("&lt;script&gt;");
  });

  test("dangerous link schemes are not turned into links", () => {
    const html = renderMarkdown("[click](javascript:alert(1)) [data](data:text/html,hi)");
    expect(html).not.toContain('href="javascript:');
    expect(html).not.toContain('href="data:text/html');
  });

  test("links open in a new tab without an opener", () => {
    const html = renderMarkdown("see https://bun.sh and [docs](https://bun.sh/docs)");
    expect(html.match(/target="_blank" rel="noopener noreferrer"/g)).toHaveLength(2);
  });
});
