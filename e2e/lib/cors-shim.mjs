/**
 * Test-side okta-sim browser shim, installed per Playwright context.
 *
 * Two simulator gaps make a real cross-origin browser flow impossible without
 * this; both fixes are response-layer only, every request still reaches the
 * real okta-sim (real discovery, real code issuance, real PKCE exchange,
 * real JWKS):
 *
 * 1. CORS. okta-sim sends no Access-Control-Allow-* headers, so a page on the
 *    SPA origin (http://localhost:5173) cannot fetch its OIDC endpoints. The
 *    shim adds the missing response headers and answers preflights. Document
 *    navigations (the redirect to /authorize) fall through untouched: they
 *    are not subject to CORS.
 *
 * 2. Dev-mode double exchange. The SPA dev server renders under React
 *    StrictMode, which double-invokes the callback effect, so TWO concurrent
 *    POSTs to /token race for one single-use authorization code: one wins,
 *    the other fails with invalid_grant and the visible flow dies about half
 *    the time. The shim coalesces identical in-flight token requests into a
 *    single wire request and hands both callers the same response, which is
 *    exactly what the production (non-StrictMode) behavior is.
 */

/**
 * Installs the shim on a Playwright browser context.
 * @param {import("@playwright/test").BrowserContext} context
 */
export function allowOktaCrossOrigin(context) {
  /** @type {Map<string, Promise<import("@playwright/test").APIResponse>>} */
  const inFlight = new Map();

  return context.route("http://localhost:8080/**", async (route) => {
    const request = route.request();
    const origin = request.headers()["origin"] ?? "*";

    if (request.method() === "OPTIONS") {
      await route.fulfill({ status: 204, headers: corsHeaders(origin) });
      return;
    }
    if (request.resourceType() === "document") {
      await route.fallback();
      return;
    }

    const response = await exchange(route, inFlight);
    await route.fulfill({ response, headers: corsHeaders(origin) });
  });
}

/**
 * Performs the request, coalescing identical in-flight POSTs to /token into
 * one wire request (see note 2 above).
 */
async function exchange(route, inFlight) {
  const request = route.request();
  if (request.method() !== "POST" || !request.url().endsWith("/token")) {
    return route.fetch();
  }
  const key = `${request.url()}#${request.postData() ?? ""}`;
  let pending = inFlight.get(key);
  if (!pending) {
    pending = route.fetch().finally(() => inFlight.delete(key));
    inFlight.set(key, pending);
  }
  return pending;
}

function corsHeaders(origin) {
  return {
    "access-control-allow-origin": origin,
    "access-control-allow-methods": "GET, POST, OPTIONS",
    "access-control-allow-headers": "authorization, content-type",
  };
}
