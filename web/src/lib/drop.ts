/** What a drop or paste carries: files, and/or a link or text for the quick-add box. */
export interface Transfer {
  files: File[];
  text: string | null;
}

/** Reads a drop or paste, preferring files, then real links (text/uri-list), then plain text. */
export function readTransfer(data: DataTransfer): Transfer {
  const files = [...data.files];
  if (files.length) return { files, text: null };

  const link = data
    .getData("text/uri-list")
    .split(/\r?\n/)
    .find((line) => line && !line.startsWith("#"));
  if (link && /^https?:/i.test(link)) return { files: [], text: link };

  const text = data.getData("text/plain").trim();
  return { files: [], text: text || null };
}

/** Whether a drag carries something we can take. */
export function isUsefulDrag(data: DataTransfer | null): boolean {
  return !!data?.types.some((t) => t === "Files" || t === "text/uri-list" || t === "text/plain");
}
