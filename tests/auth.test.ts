import { describe, expect, test } from "bun:test";
import { mkdtempSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

import { NativeLogin } from "../server/src/modules/auth/service";
import { BASE, setup } from "./helpers";

const isAllowedNativeRedirect = (raw: string) => NativeLogin.isAllowedRedirect(raw);

describe("API authentication", () => {
  test("requests without a valid session get 401", async () => {
    const { createUser, request, db } = setup();
    expect((await request(null, "GET", "/api/items")).status).toBe(401);
    expect((await request("garbage", "GET", "/api/items")).status).toBe(401);

    const expired = createUser("old", { expiresAt: new Date(Date.now() - 1000) });
    expect((await request(expired.token, "GET", "/api/items")).status).toBe(401);

    const revoked = createUser("revoked");
    expect((await request(revoked.token, "GET", "/api/me")).status).toBe(200);
    db.$client.run("DELETE FROM session WHERE token = ?", [revoked.token]);
    expect((await request(revoked.token, "GET", "/api/me")).status).toBe(401);
  });

  test("better-auth's GET endpoints still work when the web app is served", async () => {
    const webDist = mkdtempSync(join(tmpdir(), "notaekinesis-web-"));
    writeFileSync(join(webDist, "index.html"), "<!doctype html><title>app</title>");
    writeFileSync(join(webDist, "app.js"), "export {};");
    const { createUser, request } = setup({
      webDist,
      oidc: {
        providerId: "pocketid",
        discoveryUrl: "https://idp.invalid/.well-known/openid-configuration",
        clientId: "id",
        clientSecret: "secret",
        scopes: ["openid"],
        allowedGroups: [],
      },
    });
    const { userId, token } = createUser();

    const session = await request(token, "GET", "/api/auth/get-session");
    expect(session.status).toBe(200);
    expect(session.json.user.id).toBe(userId);

    // The IdP's redirect back is a GET; a bad state is better-auth's error redirect, not a 404.
    const callback = await request(null, "GET", "/api/auth/callback/pocketid?code=abc&state=nope");
    expect(callback.status).toBe(302);

    // The app shell still serves client-side routes, and unknown API paths still 404.
    expect((await request(null, "GET", "/archive")).text).toContain("<title>app</title>");
    expect((await request(null, "GET", "/app.js")).text).toBe("export {};");
    expect((await request(null, "GET", "/../package.json")).text).toContain("<title>app</title>");
    expect((await request(null, "GET", "/api/nope")).status).toBe(404);
  });

  test("/api/me returns the signed-in user", async () => {
    const { createUser, request } = setup();
    const { userId, token } = createUser("alice");
    const me = await request(token, "GET", "/api/me");
    expect(me.json).toMatchObject({ id: userId, name: "alice" });
  });
});

describe("native login", () => {
  test("only loopback and app-scheme redirects are allowed", () => {
    expect(isAllowedNativeRedirect("http://127.0.0.1:53123/cb")).toBe(true);
    expect(isAllowedNativeRedirect("http://[::1]:53123/cb")).toBe(true);
    expect(isAllowedNativeRedirect("notaekinesis://auth")).toBe(true);
    expect(isAllowedNativeRedirect("http://localhost:53123/cb")).toBe(false);
    expect(isAllowedNativeRedirect("https://evil.example/cb")).toBe(false);
    expect(isAllowedNativeRedirect("http://127.0.0.1.evil.example/cb")).toBe(false);
    expect(isAllowedNativeRedirect("http://user@127.0.0.1/cb")).toBe(false);
    expect(isAllowedNativeRedirect("javascript:alert(1)")).toBe(false);
  });

  test("rejects bad redirect targets", async () => {
    const { request } = setup();
    const res = await request(
      null,
      "GET",
      `/native-login?redirect=${encodeURIComponent("https://evil.example")}&state=abcdefgh`,
    );
    expect(res.status).toBe(400);
  });

  test("signed-in browser is sent back to the app with a one-time token it can exchange", async () => {
    const { app, createUser, request } = setup();
    const { userId, token } = createUser();
    const redirect = "http://127.0.0.1:53123/cb";

    const res = await app.handle(
      new Request(`${BASE}/native-login?redirect=${encodeURIComponent(redirect)}&state=state-1234`, {
        headers: { authorization: `Bearer ${token}` },
      }),
    );
    expect(res.status).toBe(302);
    const location = new URL(res.headers.get("location")!);
    expect(location.origin + location.pathname).toBe(redirect);
    expect(location.searchParams.get("state")).toBe("state-1234");
    const ott = location.searchParams.get("token")!;
    expect(ott).toBeTruthy();

    // The app trades the one-time token for a session token.
    const verify = await request(null, "POST", "/api/auth/one-time-token/verify", { token: ott });
    expect(verify.status).toBe(200);
    expect(verify.json.user.id).toBe(userId);
    const sessionToken: string = verify.json.session.token;
    expect((await request(sessionToken, "GET", "/api/me")).json.id).toBe(userId);

    // One-time tokens really are one-time.
    const again = await request(null, "POST", "/api/auth/one-time-token/verify", { token: ott });
    expect(again.status).toBe(400);
  });

  test("browsers cannot mint one-time tokens directly", async () => {
    const { createUser, request } = setup();
    const { token } = createUser();
    const res = await request(token, "GET", "/api/auth/one-time-token/generate");
    expect(res.status).toBe(400);
  });

  test("without a session and without an IdP the login reports it is not configured", async () => {
    const { request } = setup();
    const res = await request(
      null,
      "GET",
      `/native-login?redirect=${encodeURIComponent("notaekinesis://auth")}&state=abcdefgh`,
    );
    expect(res.status).toBe(503);
  });
});
