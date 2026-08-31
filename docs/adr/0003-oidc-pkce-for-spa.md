# ADR 0003: OIDC authorization code with PKCE for the SPA

Status: Accepted

## Context

FieldInsight (`src/Corridor.Spa`) is a React single-page application that runs entirely
in the inspector's browser, often on shared or field devices. A browser-resident client
cannot keep a client secret: anything embedded in the JavaScript bundle is public the
moment the page loads. The classic authorization-code flow without protections is
therefore open to authorization-code interception on hostile networks, and the implicit
flow (tokens in the front channel) is deprecated for exactly this reason. Shipping the
SPA as a confidential client by hiding a secret in the bundle would have been security
theater.

## Decision

Treat the SPA as an OIDC public client using the authorization-code flow with PKCE
(S256, never plain):

- okta-sim registers the client `spa` with `RequirePkce: true`, no secret, and only the
  `authorization_code` and `refresh_token` grants
  (`src/Corridor.OktaSim/Models/OAuthClient.cs`).
- The authorize endpoint rejects the client outright without a `code_challenge`, rejects
  any method but S256, and length-checks the challenge (43 to 128 characters) in
  `CheckRedirectAndScope` (`src/Corridor.OktaSim/Endpoints/Oidc.cs`).
- The token endpoint verifies the `code_verifier` with a fixed-time comparison against
  the challenge stored with the code (`AuthorizationCodeGrantAsync`).
- The client side is `oidc-client-ts` with `response_type: "code"` and PKCE on by default
  (`src/Corridor.Spa/src/auth/userManager.ts`), silent renew via refresh tokens, and
  `sessionStorage` for the user store so the session dies with the tab.

## Consequences

- No secret exists in the SPA bundle to leak; an intercepted authorization code is
  useless without the verifier, which never leaves the browser.
- The demo shows the modern browser-app pattern honestly, including refresh-token
  rotation, because the sim issues refresh tokens to public clients and enforces family
  revocation on reuse (`RefreshTokenStore.cs`).
- The SPA is a post-cutover application by design: its API endpoints on the portal
  (`/api/assignments`) accept bearer tokens only via the `SpaBearer` policy
  (`src/Corridor.Portal/Api/AssignmentsApi.cs`), which keeps the pre-cutover story clean
  (the SPA simply is not published) and the API surface single-mode.
- Coverage exists on both sides: the okta-sim unit suite (31 tests) and the SPA's Vitest
  suite (38 tests) pin the flow shapes, and the e2e suite drives the real browser login.
