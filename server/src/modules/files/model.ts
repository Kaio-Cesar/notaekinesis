import { Elysia, t, type UnwrapSchema } from "elysia";

const fileInfo = t.Object({
  id: t.String(),
  name: t.String(),
  mime: t.String(),
  size: t.Integer(),
});

export const FileModel = {
  fileInfo,
  upload: t.Object({ file: t.File() }),
  params: t.Object({ id: t.String({ format: "uuid" }) }),
} as const;

export type FileModel = { [K in keyof typeof FileModel]: UnwrapSchema<(typeof FileModel)[K]> };

export const fileModels = new Elysia({ name: "files.model" }).model({ FileInfo: fileInfo });
