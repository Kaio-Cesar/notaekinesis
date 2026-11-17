/** What the quick-add box and drag & drop turn into an item. Note text is Markdown. */
export interface Capture {
  text: string;
  url: string | null;
  title: string | null;
  tags: string[];
}

const URL_RE = /\bhttps?:\/\/[^\s<>"'`]+[^\s<>"'`.,;:!?)\]]/giu;
// `#tag` at the start of a line or after a space, followed by a space or the end of the line.
// `# Heading` (with a space) is a Markdown heading and doesn't match.
const TAG_RE = /(?<=^|[ \t])#([\p{L}\p{N}_-]{1,64})(?=[ \t]|$)/gmu;
// Parts of Markdown that must be left untouched: fenced code, inline code, links and autolinks.
const PROTECTED_RE =
  /(^|\n)(```|~~~)[\s\S]*?(\n\2[^\n]*(?=\n|$)|$)|`[^`\n]+`|!?\[[^\]\n]*\]\([^)\n]*\)|<https?:[^>\s]+>/g;

type Range = [start: number, end: number];

function protectedRanges(text: string): Range[] {
  return [...text.matchAll(PROTECTED_RE)].map((m) => [m.index, m.index + m[0].length]);
}

const inside = (ranges: Range[], start: number, end: number) => ranges.some(([a, b]) => start < b && end > a);

/**
 * Removes `text[start, end)` without disturbing the rest of the Markdown:
 * one of the surrounding spaces goes with it, and indentation elsewhere is kept.
 */
function cut(text: string, start: number, end: number): string {
  let before = text.slice(0, start);
  let after = text.slice(end);
  const atLineStart = before === "" || before.endsWith("\n");
  if (atLineStart) after = after.replace(/^[ \t]+/, "");
  else if (/^[ \t]|^\n|^$/.test(after)) before = before.replace(/[ \t]+$/, "");
  return before + after;
}

/**
 * Parses free text the way a chat box would, keeping it valid Markdown:
 * - `#tags` become tags and are removed (not inside code or links)
 * - a bare URL becomes the item's link when it's a one-liner ("nice talk https://… #video")
 *   or when it sits alone on the first line, with the Markdown body below it
 * - anything else stays in the note, where bare URLs still render as links
 */
export function parseCapture(input: string): Capture | null {
  let text = input.replace(/\r\n?/g, "\n").trim();
  if (!text) return null;

  // Tags first, removed from the end so earlier offsets stay valid.
  const tags: string[] = [];
  let ranges = protectedRanges(text);
  for (const m of [...text.matchAll(TAG_RE)].toReversed()) {
    if (inside(ranges, m.index, m.index + m[0].length)) continue;
    tags.unshift(m[1]!.toLowerCase());
    text = cut(text, m.index, m.index + m[0].length);
  }
  // Lines that held only tags are gone; don't leave a growing gap behind.
  text = text.replace(/\n{3,}/g, "\n\n").trim();

  let url: string | null = null;
  ranges = protectedRanges(text);
  const bare = [...text.matchAll(URL_RE)].filter((m) => !inside(ranges, m.index, m.index + m[0].length));
  const first = bare[0];
  if (first) {
    const firstLine = text.split("\n", 1)[0]!;
    const oneLiner = !text.includes("\n") && bare.length === 1;
    const aloneOnFirstLine = first.index === 0 && firstLine.trim() === first[0];
    if (oneLiner || aloneOnFirstLine) {
      url = first[0];
      text = cut(text, first.index, first.index + first[0].length).trim();
    }
  }

  if (!text && !url) return null;
  return { text, url, title: null, tags: [...new Set(tags)] };
}

/** Turns an item back into the text the quick-add parser reads, for editing. */
export function toCaptureText(item: { url: string | null; text: string; tags: string[] }): string {
  const tags = item.tags.map((t) => `#${t}`).join(" ");
  return [item.url, item.text, tags].filter(Boolean).join("\n\n");
}
