import { useAuth } from "../auth/AuthContext";
import { TokenClaims, decodeJwt } from "../components/TokenClaims";
import { Link } from "../router";

/**
 * Profile card: who the provider says you are, plus the collapsible ID token
 * claims pane (a deliberate demo feature: it shows exactly what okta-sim
 * issued after the migration).
 */
export function ProfileView({ onNavigate }: { onNavigate: (to: string) => void }) {
  const { user } = useAuth();
  const profile: Record<string, unknown> = user?.profile ?? {};
  const displayName = asString(profile.name) ?? "Signed-in user";
  const upn = asString(profile.upn) ?? asString(profile.preferred_username) ?? "unknown";
  const role = asString(profile.role) ?? "unknown";
  const email = asString(profile.email);
  const idToken = user?.id_token ?? "";
  const identityProvider = readIdentityProvider(idToken);

  return (
    <>
      <h1>Profile</h1>
      <p className="page-intro">
        Claims merged from your ID token and the provider&apos;s userinfo endpoint.
      </p>
      <div className="card">
        <h2>Signed-in inspector</h2>
        <dl className="facts">
          <dt>Display name</dt>
          <dd>{displayName}</dd>
          <dt>Upn</dt>
          <dd>{upn}</dd>
          <dt>Role</dt>
          <dd>{role}</dd>
          {email ? (
            <>
              <dt>Email</dt>
              <dd>{email}</dd>
            </>
          ) : null}
          <dt>Identity provider</dt>
          <dd>{identityProvider ?? "unknown"}</dd>
        </dl>
      </div>
      <div className="card">
        <h2>Token details</h2>
        {idToken ? (
          <TokenClaims token={idToken} />
        ) : (
          <p>No ID token is held in this session.</p>
        )}
        <p className="field-hint">
          The access token (used for portal API calls) refreshes silently before
          it expires; when renewal fails you are returned to the sign-in page.
        </p>
      </div>
      <p>
        <Link to="/" className="button secondary" onNavigate={onNavigate}>
          Back to assignments
        </Link>
      </p>
    </>
  );
}

function asString(value: unknown): string | null {
  return typeof value === "string" && value.length > 0 ? value : null;
}

/**
 * Reads the issuing provider off the ID token payload (the same decode the
 * claims pane below uses): the idp claim when the provider sets one, otherwise
 * the issuer. Null when no decodable token is held.
 */
function readIdentityProvider(idToken: string): string | null {
  if (!idToken) {
    return null;
  }
  try {
    const { payload } = decodeJwt(idToken);
    return asString(payload.idp) ?? asString(payload.iss);
  } catch {
    return null;
  }
}
