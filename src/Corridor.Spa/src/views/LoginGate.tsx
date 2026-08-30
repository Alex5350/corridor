import { useState } from "react";
import { useAuth } from "../auth/AuthContext";
import { demoLoginHint } from "../config";

interface LoginGateProps {
  /** Why the session ended, when the user is bounced back here. */
  message?: string | null;
}

/**
 * The sign-in gate. Everything behind it requires an okta-sim session, so
 * this is the only public screen besides the OIDC callback.
 */
export function LoginGate({ message }: LoginGateProps) {
  const { signin } = useAuth();
  const [pending, setPending] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [loginHint, setLoginHint] = useState(demoLoginHint);

  const start = async (hint: string | undefined) => {
    setPending(true);
    setError(null);
    try {
      await signin(hint && hint.length > 0 ? hint : undefined);
    } catch (cause) {
      setPending(false);
      setError(
        cause instanceof Error
          ? `Sign-in could not start: ${cause.message}`
          : "Sign-in could not start.",
      );
    }
  };

  return (
    <main className="gate-shell" id="main">
      <div className="card gate-card">
        <h1 className="gate-title">FieldInsight</h1>
        <p className="page-intro">
          Inspection assignments for Corridor field staff. Sign in with the
          Okta identity provider to load your assignments from the portal.
        </p>
        {message ? (
          <div className="notice" role="status">
            {message}
          </div>
        ) : null}
        {error ? (
          <div className="notice error" role="alert">
            {error}
          </div>
        ) : null}
        <form
          className="gate-form"
          onSubmit={(event) => {
            event.preventDefault();
            void start(loginHint);
          }}
        >
          <label htmlFor="login-hint">Username (upn)</label>
          <input
            id="login-hint"
            name="login-hint"
            type="email"
            autoComplete="username"
            value={loginHint}
            onChange={(event) => setLoginHint(event.target.value)}
          />
          <p className="field-hint">
            Sent as the login_hint parameter. The provider&apos;s page accepts any
            seeded user; demo password <code>Demo1234!</code>.
          </p>
          <button type="submit" className="button" disabled={pending}>
            {pending ? "Redirecting to provider..." : "Sign in with Okta"}
          </button>
        </form>
      </div>
    </main>
  );
}
