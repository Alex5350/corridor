import { useMemo, useState } from "react";

/**
 * Collapsible pane that decodes the header and payload segments of a JWT and
 * renders the claims as definition lists. This is a deliberate demo feature:
 * it shows exactly what okta-sim issued so the migration story (what claims
 * moved over from ADFS) is visible in the UI. Decoding is local; the token
 * never leaves the browser.
 */

export interface DecodedJwt {
  header: Record<string, unknown>;
  payload: Record<string, unknown>;
}

function base64UrlDecode(segment: string): string {
  const normalized = segment.replace(/-/g, "+").replace(/_/g, "/");
  const padded = normalized + "=".repeat((4 - (normalized.length % 4)) % 4);
  const binary = atob(padded);
  const bytes = Uint8Array.from(binary, (char) => char.charCodeAt(0));
  return new TextDecoder().decode(bytes);
}

/** Decode a JWT's non-signature segments. Throws on malformed input. */
export function decodeJwt(token: string): DecodedJwt {
  const parts = token.split(".");
  if (parts.length < 2 || !parts[0] || !parts[1]) {
    throw new Error("Not a decodable JWT: expected header.payload segments.");
  }
  const header = JSON.parse(base64UrlDecode(parts[0])) as Record<string, unknown>;
  const payload = JSON.parse(base64UrlDecode(parts[1])) as Record<string, unknown>;
  return { header, payload };
}

const epochClaims = new Set(["iat", "exp", "nbf", "auth_time"]);

function formatClaim(key: string, value: unknown): string {
  if (epochClaims.has(key) && typeof value === "number") {
    const iso = new Date(value * 1000).toISOString().replace(".000Z", "Z");
    return `${value} (${iso})`;
  }
  if (Array.isArray(value)) {
    return value.map((entry) => String(entry)).join(", ");
  }
  if (value !== null && typeof value === "object") {
    return JSON.stringify(value);
  }
  return String(value);
}

function ClaimList({ caption, claims }: { caption: string; claims: Record<string, unknown> }) {
  const entries = Object.entries(claims).sort(([a], [b]) => a.localeCompare(b));
  return (
    <div className="claims-section">
      <h4>{caption}</h4>
      <dl className="facts claims">
        {entries.map(([key, value]) => (
          <div className="claims-row" key={key}>
            <dt>{key}</dt>
            <dd>
              <code>{formatClaim(key, value)}</code>
            </dd>
          </div>
        ))}
      </dl>
    </div>
  );
}

interface TokenClaimsProps {
  token: string;
  title?: string;
}

export function TokenClaims({ token, title = "ID token claims" }: TokenClaimsProps) {
  const [open, setOpen] = useState(false);
  const panelId = "id-token-claims-panel";

  const decoded = useMemo(() => {
    try {
      return decodeJwt(token);
    } catch {
      return null;
    }
  }, [token]);

  const toggle = () => setOpen((value) => !value);

  return (
    <section className="claims-pane" aria-label={title}>
      <button
        type="button"
        className="claims-toggle"
        aria-expanded={open}
        aria-controls={panelId}
        onClick={toggle}
      >
        <span className="claims-toggle-mark" aria-hidden="true">
          {open ? "\u2212" : "+"}
        </span>
        {title}
      </button>
      <div id={panelId} className="claims-body" hidden={!open}>
        {decoded ? (
          <>
            <p className="claims-note">
              Decoded in the browser from the ID token okta-sim issued at sign-in
              (header and payload only; the signature segment is not shown).
            </p>
            <ClaimList caption="Header" claims={decoded.header} />
            <ClaimList caption="Payload claims" claims={decoded.payload} />
          </>
        ) : (
          <p className="claims-note">The token could not be decoded for display.</p>
        )}
      </div>
    </section>
  );
}
