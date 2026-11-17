/**
 * Development helper: creates (or reuses) a local "dev" user and a session,
 * then prints the session cookie so you can use the web app without an IdP.
 *
 *   bun --env-file=../.env scripts/dev-login.ts
 *
 * Paste the printed line into the browser console on the web app's origin.
 */
import { createHmac } from "node:crypto";
import { mkdirSync } from "node:fs";
import { join } from "node:path";

import { eq } from "drizzle-orm";

import { loadConfig } from "../src/config";
import { openDb } from "../src/db";
import { session, user } from "../src/db/schema";

if (process.env.NODE_ENV === "production") throw new Error("dev-login is for development only");

const config = loadConfig();
mkdirSync(config.dataDir, { recursive: true });
const db = openDb(join(config.dataDir, "notaekinesis.db"));

const email = "dev@localhost.invalid";
let userId = db.select({ id: user.id }).from(user).where(eq(user.email, email)).get()?.id;
if (!userId) {
  userId = crypto.randomUUID();
  db.insert(user).values({ id: userId, name: "Dev", email, emailVerified: true }).run();
}

const token = crypto.randomUUID().replaceAll("-", "");
db.insert(session)
  .values({ id: crypto.randomUUID(), userId, token, expiresAt: new Date(Date.now() + 7 * 86_400_000) })
  .run();

// Same format better-auth uses for its signed session cookie.
const signature = createHmac("sha256", config.authSecret).update(token).digest("base64");
const value = encodeURIComponent(`${token}.${signature}`);

console.log(`document.cookie = "better-auth.session_token=${value}; path=/; SameSite=Lax"; location.reload();`);
console.log(`\nBearer token for API clients: ${token}`);
