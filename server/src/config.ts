import { join } from "node:path";

export interface Config {
  publicUrl: string;
  port: number;
  dataDir: string;
  authSecret: string;
  oidc: {
    providerId: string;
    discoveryUrl: string;
    clientId: string;
    clientSecret: string;
    scopes: string[];
    /** When set, only users whose `groups` claim contains one of these may sign in. */
    allowedGroups: string[];
  } | null;
  /** Static web app build to serve at `/`. */
  webDist: string | null;
  /** Largest accepted upload, in bytes. */
  maxUploadBytes: number;
}

function list(value: string | undefined): string[] {
  return (value ?? "")
    .split(",")
    .map((s) => s.trim())
    .filter(Boolean);
}

export function loadConfig(env: Record<string, string | undefined> = process.env): Config {
  const publicUrl = (env.PUBLIC_URL ?? "http://localhost:3000").replace(/\/$/, "");
  const authSecret = env.BETTER_AUTH_SECRET;
  if (!authSecret || authSecret.length < 32) {
    throw new Error("BETTER_AUTH_SECRET must be set to at least 32 characters (e.g. `openssl rand -base64 32`).");
  }

  const discoveryUrl = env.OIDC_DISCOVERY_URL;
  const oidc = discoveryUrl
    ? {
        providerId: env.OIDC_PROVIDER_ID ?? "homelab",
        discoveryUrl,
        clientId: env.OIDC_CLIENT_ID ?? "",
        clientSecret: env.OIDC_CLIENT_SECRET ?? "",
        scopes: list(env.OIDC_SCOPES ?? "openid,profile,email"),
        allowedGroups: list(env.OIDC_ALLOWED_GROUPS),
      }
    : null;
  if (oidc && (!oidc.clientId || !oidc.clientSecret)) {
    throw new Error("OIDC_CLIENT_ID and OIDC_CLIENT_SECRET are required when OIDC_DISCOVERY_URL is set.");
  }

  return {
    publicUrl,
    port: Number(env.PORT ?? 3000),
    dataDir: env.DATA_DIR ?? join(process.cwd(), "data"),
    authSecret,
    oidc,
    webDist: env.WEB_DIST ?? null,
    maxUploadBytes: Number(env.MAX_UPLOAD_MB ?? 100) * 1024 * 1024,
  };
}
