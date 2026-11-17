import { mkdirSync } from "node:fs";
import { join } from "node:path";

import { createApp } from "./app";
import { loadConfig } from "./config";
import { openDb } from "./db";

const config = loadConfig();
mkdirSync(config.dataDir, { recursive: true });
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
