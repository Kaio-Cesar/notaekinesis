/** What the server accepts (it checks the actual bytes; this is just for the picker and early feedback). */
export const ACCEPT =
  "image/png,image/jpeg,image/gif,image/webp,image/avif,video/mp4,video/webm,video/quicktime,application/pdf";

const accepted = new Set(ACCEPT.split(","));

export function isSupportedFile(file: File): boolean {
  return accepted.has(file.type);
}

export type MediaKind = "image" | "video" | "pdf" | "other";

export function mediaKind(mime: string): MediaKind {
  if (mime.startsWith("image/")) return "image";
  if (mime.startsWith("video/")) return "video";
  if (mime === "application/pdf") return "pdf";
  return "other";
}

export const fileUrl = (id: string) => `/api/files/${id}`;

export function formatSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  const units = ["KB", "MB", "GB"];
  let value = bytes / 1024;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }
  return `${value < 10 ? value.toFixed(1) : Math.round(value)} ${units[unit]}`;
}
