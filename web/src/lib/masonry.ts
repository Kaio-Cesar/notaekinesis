/** What the masonry layout needs to know about an item to guess its height. */
export interface Sized {
  text: string;
  url: string | null;
  description: string | null;
  tags: string[];
  file?: { mime: string } | null;
}

function fileHeight(mime: string | undefined): number {
  if (!mime) return 0;
  // Images and videos vary; ~10 lines is a typical landscape picture in a grid column.
  return mime.startsWith("image/") || mime.startsWith("video/") ? 10 : 2.5;
}

/** Rough card height in "lines". Only used to balance columns, so it doesn't need to be exact. */
export function estimateHeight(item: Sized, charsPerLine = 38, maxTextLines = 12): number {
  const textLines = item.text
    ? item.text.split("\n").reduce((sum, line) => sum + Math.max(1, Math.ceil(line.length / charsPerLine)), 0)
    : 0;
  return (
    2 + // padding and the footer
    (item.url ? 1.2 : 0) +
    fileHeight(item.file?.mime) +
    (item.description ? 2 : 0) +
    Math.min(textLines, maxTextLines) +
    (item.tags.length > 3 ? 0.8 : 0)
  );
}

/**
 * Splits items into columns, placing each one in the currently shortest column.
 * Items keep their order reading left to right, top to bottom, like Google Keep,
 * instead of running down one column first as CSS `columns` would.
 */
export function masonry<T extends Sized>(items: T[], columnCount: number): T[][] {
  const count = Math.max(1, columnCount);
  const columns: T[][] = Array.from({ length: count }, () => []);
  const heights = Array.from({ length: count }, () => 0);
  for (const item of items) {
    let target = 0;
    for (let i = 1; i < count; i++) if (heights[i]! < heights[target]!) target = i;
    columns[target]!.push(item);
    heights[target]! += estimateHeight(item);
  }
  return columns;
}
