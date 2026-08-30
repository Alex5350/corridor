# FieldInsight (Corridor.Spa)

The inspector single page app for the Corridor identity migration demo. React 19
+ Vite + TypeScript, no UI framework, one plain stylesheet, federal-plain look
shared with the portal.

Sign-in is a real OIDC public client flow against okta-sim: authorization code
+ PKCE S256 via `oidc-client-ts`, RS256 ID tokens validated against the
provider's JWKS, silent renew through the refresh token grant (access tokens
last 15 minutes), and a return to the sign-in gate with a message when renewal
fails. Assignments come from the portal REST API with the okta access token as
a bearer token; checklist toggles PATCH optimistically and roll back on
failure with an inline retry.

## Run it

Prereqs: okta-sim on `http://localhost:8080`, portal on `http://localhost:5200`
(same repo). Then:

```
npm install
npm run dev        # http://localhost:5173 (strictPort)
```

Sign in with a seeded user, for example `inspector@corridor.example`,
password `Demo1234!` (documented on the provider's login page too).

Other scripts:

```
npm run build      # tsc -b + vite build
npm run lint       # eslint (typescript-eslint, react-hooks), zero warnings
npm test           # vitest
npm run preview    # serve the production build on 4173
```

## Notes

- Client registration (in okta-sim): client id `spa`, public, PKCE required,
  redirect `http://localhost:5173/callback`, post-logout `http://localhost:5173/`,
  scopes `openid profile email offline_access`.
- The signed-in user is persisted in `sessionStorage` (documented demo
  choice): reloads keep the session, closing the tab ends it.
- The profile page decodes and shows the ID token header and payload claims.
  That is deliberate: the demo is about what the provider issues.
- `vite.config.ts` documents the CSP-lite response headers. Strict policy for
  the built app; the dev server adds `'unsafe-inline'` for scripts only
  because the React fast-refresh preamble is an inline module and Vite has no
  nonce hook for it. `'unsafe-eval'` is never granted.
- Endpoints can be overridden with `VITE_OIDC_AUTHORITY`,
  `VITE_OIDC_CLIENT_ID`, `VITE_OIDC_REDIRECT_URI`,
  `VITE_OIDC_POST_LOGOUT_URI`, and `VITE_PORTAL_API`.

Synthetic data only. No real agency systems, no real secrets.
