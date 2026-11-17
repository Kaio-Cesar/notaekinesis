import { Elysia } from "elysia";

import { authGuard } from "../auth";
import type { Auth } from "../auth/service";
import { FileModel, fileModels } from "./model";
import type { FileService } from "./service";

/** Upload a file, then create an item with its `fileId`. Files are served back with range support. */
export const filesController = ({ auth, files }: { auth: Auth; files: FileService }) =>
  new Elysia({ name: "files.controller", prefix: "/api/files", detail: { tags: ["files"] } })
    .use(fileModels)
    .use(authGuard(auth))
    .guard({ auth: true }, (app) =>
      app
        .post("/", ({ user, body: { file }, status }) => files.save(user.id, file).then((info) => status(201, info)), {
          body: FileModel.upload,
          response: { 201: "FileInfo" },
          detail: { description: "Images (PNG, JPEG, GIF, WebP, AVIF), videos (MP4, WebM, MOV) and PDFs." },
        })
        .get("/:id", ({ user, params: { id }, request }) => files.serve(user.id, id, request.headers.get("range")), {
          params: FileModel.params,
        }),
    );
