/**
 * Central integration endpoints for the FieldInsight SPA.
 *
 * Defaults match the Corridor demo topology: okta-sim on 8080 (the OIDC
 * authority), the portal API on 5200, and this dev server on 5173. Every value
 * can be overridden with a VITE_ env var so the same build works against other
 * ports without touching code.
 */

function envOr(name: string, fallback: string): string {
  const value = import.meta.env[name];
  return typeof value === "string" && value.length > 0 ? value : fallback;
}

export const config = {
  /** okta-sim base URL, also the OIDC issuer (issuer == authority here). */
  authority: envOr("VITE_OIDC_AUTHORITY", "http://localhost:8080"),
  /** Registered public client id in okta-sim (PKCE S256 required). */
  clientId: envOr("VITE_OIDC_CLIENT_ID", "spa"),
  /** Registered redirect URI for the authorization code response. */
  redirectUri: envOr("VITE_OIDC_REDIRECT_URI", "http://localhost:5173/callback"),
  /** Registered post-logout landing page in okta-sim. */
  postLogoutRedirectUri: envOr("VITE_OIDC_POST_LOGOUT_URI", "http://localhost:5173/"),
  /** Portal base URL hosting /api/assignments. */
  portalApi: envOr("VITE_PORTAL_API", "http://localhost:5200"),
} as const;

/** Scope set allowed for the spa client in okta-sim's client registry. */
export const oidcScope = "openid profile email offline_access";

/** Demo login hint shown on the sign-in gate (okta-sim honors login_hint). */
export const demoLoginHint = "inspector@corridor.example";
