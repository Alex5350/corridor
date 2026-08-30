import { useEffect, useState } from "react";
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

  useEffect(() => {
    let cancelled = false;
    manager
      .signinCallback()
      .then(() => {
        if (!cancelled) {
          window.location.replace("/");
        }
      })
      .catch((cause: unknown) => {
        if (!cancelled) {
          setError(cause);
        }
      });
    return () => {
      cancelled = true;
    };
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
