import { useEffect, useRef, useState } from "react";
import type { UserManager } from "oidc-client-ts";
import { ErrorPanel } from "../components/ErrorPanel";
import { Link } from "../router";

interface CallbackPageProps {
  manager: UserManager;
}

/**
 * Handles the redirect back from okta-sim: validates state and PKCE, exchanges
 * the code, stores the user, then lands on the assignments list.
 */
export function CallbackPage({ manager }: CallbackPageProps) {
  const [error, setError] = useState<unknown>(null);
  const exchanged = useRef(false);

  useEffect(() => {
    // One-shot latch. React StrictMode double-invokes effects under the dev
    // server, and a second signinCallback() would send a second POST /token
    // for the same single-use authorization code: one exchange always fails
    // with invalid_grant. The ref survives the StrictMode remount, so the
    // second invocation no-ops and exactly one exchange happens.
    if (exchanged.current) {
      return;
    }
    exchanged.current = true;
    manager
      .signinCallback()
      .then(() => {
        window.location.replace("/");
      })
      .catch((cause: unknown) => {
        setError(cause);
      });
  }, [manager]);

  return (
    <main className="shell" id="main">
      <h1>Completing sign-in</h1>
      {error ? (
        <>
          <ErrorPanel error={error} />
          <p>
            <Link to="/" className="button secondary">
              Back to sign in
            </Link>
          </p>
        </>
      ) : (
        <p role="status">Validating the sign-in response from the identity provider...</p>
      )}
    </main>
  );
}
