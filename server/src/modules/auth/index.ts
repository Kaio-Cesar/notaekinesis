import { Elysia } from "elysia";

import type { Config } from "../../config";
import { AuthModel, authModels } from "./model";
import { NativeLogin, type Auth } from "./service";

/**
 * Request-dependent service: resolves the session from the cookie or the
 * `Authorization: Bearer` header. Routes opt in with `{ auth: true }`.
 * Named, so every controller that uses it shares one instance.
 */
export const authGuard = (auth: Auth) =>
  new Elysia({ name: "auth.guard" }).macro({
    auth: {
      async resolve({ status, request: { headers } }) {
        const session = await auth.api.getSession({ headers });
        if (!session) return status(401, "Unauthorized");
        return { user: session.user, session: session.session };
      },
    },
  });

/** better-auth's own endpoints (/api/auth/*), the current user, and the native-app login. */
export const authController = ({ auth, config }: { auth: Auth; config: Config }) =>
  new Elysia({ name: "auth.controller" })
    .use(authModels)
    .use(authGuard(auth))
    .group("/api", (api) =>
      api
        .mount(auth.handler)
        .get("/me", ({ user }) => ({ id: user.id, name: user.name, email: user.email, image: user.image ?? null }), {
          auth: true,
          response: "Me",
          detail: { tags: ["auth"] },
        }),
    )
    .get(
      "/native-login",
      async ({ request: { headers }, query, redirect, set }) => {
        const { location, cookies } = await NativeLogin.start(auth, config, headers, query);
        set.headers["cache-control"] = "no-store";
        if (cookies.length > 0) set.headers["set-cookie"] = cookies;
        return redirect(location, 302);
      },
      { query: AuthModel.nativeLoginQuery, detail: { hide: true } },
    );
