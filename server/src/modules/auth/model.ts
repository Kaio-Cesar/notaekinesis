import { Elysia, t, type UnwrapSchema } from "elysia";

export const AuthModel = {
  nativeLoginQuery: t.Object({
    redirect: t.String({ maxLength: 2048 }),
    state: t.String({ minLength: 8, maxLength: 256 }),
  }),
  me: t.Object({
    id: t.String(),
    name: t.String(),
    email: t.String(),
    image: t.Nullable(t.String()),
  }),
  /** Public settings the sign-in page needs before anyone is logged in. */
  config: t.Object({
    /** better-auth provider id to pass to `signIn.social`, or null when no IdP is configured. */
    providerId: t.Nullable(t.String()),
  }),
} as const;

export type AuthModel = { [K in keyof typeof AuthModel]: UnwrapSchema<(typeof AuthModel)[K]> };

export const authModels = new Elysia({ name: "auth.model" }).model({ Me: AuthModel.me, AuthConfig: AuthModel.config });
