import react from "@vitejs/plugin-react";
import { defineConfig } from "vitest/config";

/**
 * CSP-lite response headers.
 *
 * The production/preview policy is strict: everything same-origin except the
 * two Corridor services this SPA talks to (okta-sim for OIDC, the portal for
 * the assignments API), plus the dev HMR websocket. 'unsafe-eval' is never
 * allowed anywhere: nothing in this app or its dependencies needs it.
 *
 * connect-src http://localhost:8080  -> OIDC discovery, JWKS, token, userinfo
 * connect-src http://localhost:5200  -> portal assignments API
 * frame-src   http://localhost:8080  -> silent-renew iframe fallback
 * form-action http://localhost:8080  -> (browser safety net for the redirect)
 */
const csp = [
  "default-src 'self'",
  "script-src 'self'",
  "style-src 'self'",
  "img-src 'self' data:",
  "font-src 'self'",
  "connect-src 'self' http://localhost:8080 http://localhost:5200 ws://localhost:5173",
  "frame-src 'self' http://localhost:8080",
  "object-src 'none'",
  "base-uri 'self'",
  "form-action 'self' http://localhost:8080",
  "frame-ancestors 'none'",
].join("; ");

const strictHeaders = {
  "Content-Security-Policy": csp,
  "X-Content-Type-Options": "nosniff",
  "X-Frame-Options": "DENY",
  "Referrer-Policy": "no-referrer",
};

/**
 * Dev-only addition: 'unsafe-inline' for scripts. The React plugin injects a
 * small inline module into index.html to bootstrap fast refresh, and Vite has
 * no nonce mechanism for it. Scripts from other origins stay blocked and
 * 'unsafe-eval' stays out; the strict header above governs preview/production.
 */
const devCsp = csp.replace("script-src 'self'", "script-src 'self' 'unsafe-inline'");

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    strictPort: true,
    headers: { ...strictHeaders, "Content-Security-Policy": devCsp },
  },
  preview: {
    port: 4173,
    strictPort: true,
    headers: strictHeaders,
  },
  test: {
    environment: "jsdom",
    setupFiles: ["./src/test/setup.ts"],
  },
});
