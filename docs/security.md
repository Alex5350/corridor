# Corridor security notes

A threat-oriented short document: what in this repo is deliberately demo-grade and must
never be treated as production practice, and which patterns are production-hardened and
worth copying. The findings ledger with per-finding fixes is
[security-findings-log.md](security-findings-log.md).

## What is intentionally demo-grade

Named honestly, so nobody copies the wrong half:

- **Password hashing**: `idn.Users.PasswordHash` is a plain SHA-256 of
  `corridor-demo-` + password (`db/sql/seed/003_seed.sql`). Fast, unsalted, demo-only.
  Production uses a memory-hard KDF (argon2, scrypt, or bcrypt) with per-user salts.
- **Secrets in containers and config**: the client secrets `corridor-portal-secret` and
  `corridor-legacy-secret`, the SCIM bearer `corridor-scim-token`, and the SQL sa
  password `CorridorDev1!` are documented constants committed deliberately
  (`docker-compose.yml`, `appsettings.Development.json`, `ClientRegistry`). They have no
  secret value; production secrets arrive via a secret manager or at minimum environment
  variables, never in files.
- **Plain http everywhere**: every URL is `http://localhost:<port>`; TLS termination,
  HSTS, and secure cookie flags are out of scope for a local simulation. The OIDC and
  bearer handlers therefore set `RequireHttpsMetadata = false` (marked in
  `src/Corridor.Portal/Program.cs`), which is correct only for local sims.
- **Rate limiting is present but coarse**: okta-sim throttles the authorize and token
  endpoints with a per-IP fixed window (60 per minute; 429 responses). adfs-sim's login
  form does not throttle and nothing implements lockout or monitoring: production wraps
  all credential surfaces in smarter limits plus lockout.
- **Committed dev certificates** under `certs/` sign synthetic tokens and protect
  nothing (`certs/README.md`).
- **Bearer-token-in-error-detail style conveniences**: the SCIM 401 names its demo token
  to make the demo self-explaining; production never teaches callers the credential.
- **No real PII**: every user, licensee, case, and assignment is synthetic; this is a
  boundary, not a shortcut ([ADR 0001](adr/0001-simulate-both-providers-locally.md)).

## Production-hardened patterns demonstrated

The half worth copying:

- **PKCE S256, enforced server-side**: mandatory for public clients, method and length
  checked, fixed-time verifier comparison (`src/Corridor.OktaSim/Endpoints/Oidc.cs`;
  [ADR 0003](adr/0003-oidc-pkce-for-spa.md)).
- **Single-use, short-lived authorization codes**: atomically consumed, five-minute
  lifetime, client and redirect_uri bound (`AuthCodeStore.cs`).
- **Refresh-token rotation with family revocation**: replaying a rotated token revokes
  the whole family (`RefreshTokenStore.cs`); the token endpoint reports reuse as
  `invalid_grant`.
- **Signed XML with XXE-safe parsing**: DTD prohibited, no resolver, on every SAML and
  XACML parse path (`src/Corridor.OktaSim/Saml/SafeXml.cs`, used by `PdpEngine` and the
  sims' builders; the legacy inspector sets `XmlResolver = null`).
- **Signature, audience, and lifetime validation on both token kinds**: SAML
  (certificate-pinned, NotBefore with skew, NotOnOrAfter) and JWT (RS256 via JWKS,
  issuer, audience, one-minute skew, algorithm pinned) in
  `src/Corridor.Legacy/Security/`.
- **Registered redirect enforcement**: unregistered `redirect_uri` gets a plain 400,
  never a redirect (open-redirect avoidance, `CheckRedirectAndScope`).
- **Default-deny authorization**: the XACML deny-all policy sorts last; malformed or
  unmatched requests are Deny with a reason, never a silent pass
  ([ADR 0007](adr/0007-xacml-central-pdp.md)).
- **Transition guards in SQL**: illegal case moves are refused inside
  `trace.usp_UpdateStatus` with error state 40001, applying to every caller
  ([ADR 0005](adr/0005-keep-raw-adonet.md)).
- **Audit events on every trust change**: mode and audit writes happen together
  (`TrustModeService.FlipAsync`).
- **Error discipline**: RFC 9457 problem details on REST, named `cor:` subcodes on SOAP
  faults, XACML Deny-with-reason on PDP errors; nothing leaks stack traces to callers.
- **Parameterized data access everywhere**: ADO.NET with `SqlParameter` only; no
  concatenated SQL in any service.

## Findings and scanning

Static and web scanning are configured in `sonar-project.properties` and run outside CI
by choice (CI stays fast and dependency free). Findings, severities, and fixes are
tracked to closure in [security-findings-log.md](security-findings-log.md); every entry
there is Resolved. Vulnerability reports for this demo: open an issue; there is no real
data or service to protect, but regressions in the hardened patterns above are treated
as defects.
