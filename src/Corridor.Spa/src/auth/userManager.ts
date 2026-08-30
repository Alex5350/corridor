import { Log, UserManager, WebStorageStateStore } from "oidc-client-ts";
import { config, oidcScope } from "../config";

/**
 * The OIDC public client against okta-sim.
 *
 * - Authorization code flow with PKCE S256: oidc-client-ts derives the code
 *   verifier and challenge automatically for public clients (disablePKCE
 *   defaults to false), which is what okta-sim mandates for the "spa" client.
 * - Silent renew: okta-sim issues refresh tokens on the code exchange, so the
 *   library renews via the refresh_token grant before the 15 minute access
 *   token expires (automaticSilentRenew).
 * - monitorSession is off: okta-sim does not publish a check_session_iframe,
 *   so there is nothing to monitor.
 * - userStore is sessionStorage: a deliberate demo choice. The signed-in user
 *   survives reloads in the tab but dies with it, keeping the demo honest
 *   about SPA session lifetime without leaving tokens in durable storage.
 */
export function createUserManager(): UserManager {
  return new UserManager({
    authority: config.authority,
    client_id: config.clientId,
    redirect_uri: config.redirectUri,
    post_logout_redirect_uri: config.postLogoutRedirectUri,
    response_type: "code",
    scope: oidcScope,
    automaticSilentRenew: true,
    monitorSession: false,
    userStore: new WebStorageStateStore({ store: window.sessionStorage }),
  });
}

/** Only log protocol-level errors from the library; keep the console clean. */
Log.setLevel(Log.ERROR);
