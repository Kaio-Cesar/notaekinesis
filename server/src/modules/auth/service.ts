import { betterAuth } from "better-auth";
import { drizzleAdapter } from "better-auth/adapters/drizzle";
import { APIError } from "better-auth/api";
import { bearer } from "better-auth/plugins/bearer";
import { genericOAuth } from "better-auth/plugins/generic-oauth";
import { oneTimeToken } from "better-auth/plugins/one-time-token";
import { status } from "elysia";

import type { Config } from "../../config";
import { schema, type Db } from "../../db";
import type { AuthModel } from "./model";

const DAY = 60 * 60 * 24;

export function createAuth(db: Db, config: Config) {
  const { oidc } = config;

  return betterAuth({
    baseURL: config.publicUrl,
    basePath: "/api/auth",
    secret: config.authSecret,
    trustedOrigins: [config.publicUrl],
    database: drizzleAdapter(db, { provider: "sqlite", schema }),
    emailAndPassword: { enabled: false },
    session: {
      expiresIn: 90 * DAY,
      updateAge: DAY, // sliding expiry: refreshed at most once a day while in use
    },
    account: { accountLinking: { enabled: true, trustedProviders: oidc ? [oidc.providerId] : [] } },
    plugins: [
      bearer(),
      // Native clients receive a one-time token after the browser login and
      // trade it for a session token. Only the server may mint them.
      oneTimeToken({ expiresIn: 2, disableClientRequest: true, disableSetSessionCookie: true, storeToken: "hashed" }),
      ...(oidc
        ? [
            genericOAuth({
              config: [
                {
                  providerId: oidc.providerId,
                  discoveryUrl: oidc.discoveryUrl,
                  clientId: oidc.clientId,
                  clientSecret: oidc.clientSecret,
                  scopes: oidc.scopes,
                  pkce: true,
                  overrideUserInfo: true,
                  mapProfileToUser(profile) {
                    const p = profile as Record<string, unknown>;
                    if (oidc.allowedGroups.length > 0) {
                      const groups = Array.isArray(p.groups) ? p.groups.map(String) : [];
                      if (!groups.some((g) => oidc.allowedGroups.includes(g))) {
                        throw new APIError("FORBIDDEN", { message: "You are not in a group allowed to use this app." });
                      }
                    }
                    return {
                      name: String(p.name ?? p.preferred_username ?? p.email ?? "user"),
                      email: String(p.email ?? `${String(p.sub)}@users.invalid`),
                      emailVerified: true,
                    };
                  },
                },
              ],
            }),
          ]
        : []),
    ],
  });
}

export type Auth = ReturnType<typeof createAuth>;

export const NATIVE_APP_SCHEME = "notaekinesis:";

/**
 * Browser login for the native apps. The app opens `/native-login` in the
 * system browser; once signed in, the browser is sent back to the app with a
 * one-time token, which the app trades for a session token at
 * POST /api/auth/one-time-token/verify.
 */
export abstract class NativeLogin {
  /** Only a loopback address (RFC 8252 §7.3) or the app's own scheme may receive tokens. */
  static isAllowedRedirect(raw: string): boolean {
    let url: URL;
    try {
      url = new URL(raw);
    } catch {
      return false;
    }
    if (url.username || url.password) return false;
    if (url.protocol === "http:") return url.hostname === "127.0.0.1" || url.hostname === "[::1]";
    return url.protocol === NATIVE_APP_SCHEME;
  }

  /** Returns where to send the browser next, plus any cookies to set on the way. */
  static async start(
    auth: Auth,
    config: Config,
    headers: Headers,
    { redirect, state }: AuthModel["nativeLoginQuery"],
  ): Promise<{ location: string; cookies: string[] }> {
    if (!NativeLogin.isAllowedRedirect(redirect)) throw status(400, "Invalid redirect");

    const session = await auth.api.getSession({ headers });
    if (!session) {
      if (!config.oidc) throw status(503, "No identity provider is configured");
      const callbackURL = `/native-login?${new URLSearchParams({ redirect, state })}`;
      // genericOAuth providers are registered as social providers.
      const res = await auth.api.signInSocial({
        body: { provider: config.oidc.providerId as "github", callbackURL, disableRedirect: true },
        headers,
        asResponse: true,
      });
      const { url } = (await res.json()) as { url: string };
      return { location: url, cookies: res.headers.getSetCookie() };
    }

    const { token } = await auth.api.generateOneTimeToken({ headers });
    const target = new URL(redirect);
    target.searchParams.set("token", token);
    target.searchParams.set("state", state);
    return { location: target.toString(), cookies: [] };
  }
}
