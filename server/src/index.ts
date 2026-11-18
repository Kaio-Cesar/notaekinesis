import { mkdirSync } from "node:fs";
import { join, resolve } from "node:path";

import { createApp } from "./app";
import { loadConfig } from "./config";
import { openDb } from "./db";

const config = loadConfig();
try {
  mkdirSync(config.dataDir, { recursive: true });
} catch (error) {
  // Usually Docker: a DATA_DIR from .env pointing inside the image instead of at the /data volume.
  console.error(
    `Can't create the data folder ${resolve(config.dataDir)} (DATA_DIR=${process.env.DATA_DIR ?? "unset"}). ` +
      "Point DATA_DIR at a folder this user can write to; in Docker, leave it unset to use /data.",
  );
  throw error;
}
const db = openDb(join(config.dataDir, "notaekinesis.db"));

const app = createApp({ db, config }).listen({
  port: config.port,
  // SSE connections send a heartbeat every 20 s; keep idle sockets open longer than that.
  idleTimeout: 60,
  // Room for the largest upload plus the multipart envelope.
  maxRequestBodySize: config.maxUploadBytes + 1024 * 1024,
});

console.log(`notaekinesis listening on ${app.server?.url} (public URL ${config.publicUrl})`);
if (!config.oidc) console.warn("OIDC_DISCOVERY_URL is not set: nobody can sign in.");
