# Corridor: the engineering view

The companion to the [README's product story](README.md): the architecture, the request
paths, the stack, and every major engineering decision traced back to the identity migration
it exists to pull off. The migration plan itself, with phases and rollback, lives in
[docs/migration-plan.md](docs/migration-plan.md); this page explains how the machinery works
and where each piece lives in the repo.

## Architecture

![Corridor architecture: two simulated identity providers on the left (adfs-sim issuing signed SAML 2.0 assertions, okta-sim issuing OIDC code plus PKCE, SAML, SCIM 2.0, and XACML decisions); three applications in the middle (the PermitPortal MVC web app, the FieldInsight React SPA, the TraceLink SOAP service) plus the portal's REST-to-SOAP bridge; SQL Server on the right holding the perm, trace, and idn schemas with transition-guarding stored procedures and migration state; the VB ops tool and the Postman, SoapUI, and JMeter regression suite operating from below and above](docs/diagrams/architecture.svg)

In flow order:

- **adfs-sim** (`src/Corridor.AdfsSim`, port 8090): the legacy provider. An ASP.NET Core
  MVC app that serves real SAML 2.0 federation metadata, a forms-style login page seeded
  from `idn.Users`, and an SSO POST endpoint at `/adfs/ls` that answers with signed
  assertions (`Saml/SamlResponseBuilder.cs`, `Saml/SamlSigner.cs`).
- **okta-sim** (`src/Corridor.OktaSim`, port 8080): the target provider. Minimal APIs
  implementing real OIDC (discovery, authorize with mandatory PKCE for public clients,
  token with refresh rotation and client credentials, JWKS with a rotating kid, userinfo,
  logout), a SAML IdP mode for the portal's dual-trust window, SCIM 2.0 provisioning, an
  XACML policy decision point, and a read-only admin persona UI
  (`Endpoints/Oidc.cs`, `Endpoints/Saml.cs`, `Endpoints/Scim.cs`, `Endpoints/Xacml.cs`).
- **PermitPortal** (`src/Corridor.Portal`, port 5200): the web application. Razor Pages
  with an OIDC confidential client against okta-sim, a SAML assertion consumer service at
  `/saml/acs` that honors the current TrustMode (`Api/SamlAcsEndpoint.cs`,
  `Auth/LoginRoute.cs`), and the Admin > Migration dashboard that flips TrustMode live
  (`Pages/Admin/Migration.cshtml.cs`).
- **FieldInsight SPA** (`src/Corridor.Spa`, port 5173): React 19 + Vite, an OIDC public
  client using `oidc-client-ts` with PKCE (`src/auth/userManager.ts`), consuming
  bearer-only assignment endpoints on the portal.
- **TraceLink** (`src/Corridor.Legacy`, port 8000): the legacy SOAP 1.1 service on
  CoreWCF, WSDL at `/TraceLink.svc?wsdl`, raw ADO.NET against stored procedures, and a
  dispatch inspector that validates a WS-Security-style identity header per TrustMode
  (`Security/CorridorSecurityMessageInspector.cs`, `Security/TokenValidator.cs`).
- **REST-to-SOAP bridge**: the portal's `/api/cases` JSON endpoints over its own SOAP
  client, translating REST calls to TraceLink envelopes and faults to RFC 9457 problem
  details (`Api/CasesApi.cs`, `Services/TraceLink/SoapTraceLinkClient.cs`).
- **SQL Server** (docker compose service `db`): the `perm`, `trace`, and `idn` schemas,
  the transition-guarding trace procedures, and the migration state plus audit trail
  (`db/sql/001_schemas.sql`, `db/sql/002_trace_procs.sql`).
- **Ops tool** (`src/Corridor.Ops.Tool`): the VB.NET console tool for metadata checks,
  token decode/validate, and SCIM dumps during a flip window.
- **API regression**: the committed Postman, SoapUI, and JMeter artifacts run between
  every migration phase ([ADR 0008](docs/adr/0008-regression-toolchain.md)).

The whole estate runs from one `docker compose --profile full` invocation
(`docker-compose.yml`, driven by `scripts/dev-up.sh`). The cutover state machine lives in
the database, not in any binary, which is what makes the flip reversible without a
redeploy ([ADR 0002](docs/adr/0002-dual-trust-cutover.md)).

## How the tech solves the business problem

| Business problem | Engineering decision | Why this tech | What it buys | Where documented |
|---|---|---|---|---|
| The agency cannot afford a login outage for three applications | Per-app TrustMode state machine in SQL (Adfs -> Dual -> Okta) with every flip audited | Mode in the database means a running-system change, not a redeploy | A cutover with no downtime window and one-click rollback per app | [ADR 0002](docs/adr/0002-dual-trust-cutover.md), `Services/TrustModeService.cs`, `idn.MigrationApps` in `db/sql/001_schemas.sql` |
| A browser app cannot keep a secret, so classic code flow is interception-prone | OIDC public client with PKCE S256, mandatory at the provider | The verifier never leaves the browser, so an intercepted code is worthless | Inspectors sign in from field devices without a leaked-secret class of incident | [ADR 0003](docs/adr/0003-oidc-pkce-for-spa.md), `Endpoints/Oidc.cs` (CheckRedirectAndScope, AuthorizationCodeGrantAsync) |
| Accounts must move with the cutover or first logins fail | SCIM 2.0 provisioning bridge writing through to the same user store the sims validate against | A standard API beats bespoke CSV both directions and audits naturally | Users exist, with the right role, before their first modern login | [ADR 0006](docs/adr/0006-scim-provisioning-bridge.md), `Endpoints/Scim.cs`, `tests/Corridor.IntegrationTests/ScimProvisioningTests.cs` |
| Authorization rules scattered per app cannot survive a claim-shape change | Central XACML PDP with file-based policies and default deny | Rules become one reviewable artifact instead of three codebases | Who-can-do-what is answerable, and auditable, from `policies/` | [ADR 0007](docs/adr/0007-xacml-central-pdp.md), `Xacml/PdpEngine.cs`, `policies/*.xacml.xml` |
| The SOAP service cannot be rewritten on this schedule | Keep the WSDL contract; swap the identity inside a WS-Security-style header that accepts SAML or JWT | Callers keep working; identity becomes header content gated by TrustMode | The riskiest asset crosses to modern identity with zero caller changes | [ADR 0004](docs/adr/0004-token-header-for-soap.md), `Security/CorridorSecurityMessageInspector.cs`, `Security/TokenValidator.cs` |
| Rewriting the data layer mid-migration doubles the blast radius | Raw ADO.NET preserved untouched; new identity state in a separate `idn` schema | Behavioral deltas become attributable to identity work by construction | The legacy data layer needs no code-freeze exception during cutover | [ADR 0005](docs/adr/0005-keep-raw-adonet.md), `DataAccess/TraceCaseRepository.cs` |
| Status rules must stay enforceable and auditable regardless of caller | Legal trace transitions enforced inside `trace.usp_UpdateStatus`, error state 40001 mapped to a `cor:` fault | Rules in SQL apply to every caller, present or future | The audit-grade rulebook survives the migration untouched | [ADR 0005](docs/adr/0005-keep-raw-adonet.md), `db/sql/002_trace_procs.sql`, `DataAccess/SqlFaultMapper.cs` |
| Mid-flip confidence needs a verdict from outside this codebase | Committed Postman + SoapUI + JMeter regression artifacts run between every phase | Independent tools reading the same contracts catch what in-repo tests cannot | Each phase gate is a green run anyone can reproduce | [ADR 0008](docs/adr/0008-regression-toolchain.md), `docs/test-plan.md`, the `artifacts` job in `.github/workflows/ci.yml` |
| The demo must never depend on tenants, trials, or real directories | Both providers simulated locally with real protocols ([ADR 0001](docs/adr/0001-simulate-both-providers-locally.md)) | The risk being demonstrated is protocol behavior, which the sims implement for real | The whole migration runs on one laptop; swap paths documented for production | [ADR 0001](docs/adr/0001-simulate-both-providers-locally.md), `docs/runbook.md` |

The row that shaped everything else is the dual-trust cutover. Once the promise is "no
downtime window", almost every other decision falls out of it: the mode must live outside
the binaries (SQL), the flip must be per-app and reversible (the state machine plus audit
rows), both providers must be demonstrable side by side (the sims), and the SOAP service
must accept both token kinds simultaneously during the window (the header-plus-validator
design). It also produced the most instructive bug of the build: the portal's SOAP client
initially emitted its `cor:Security` header in the service contract namespace and prefixed
the `jwt` element, and every bridge call came back `cor:MissingSecurityHeader` until the
client was aligned with the WSDL-observant inspector. The full debugging story, plus the
DataContract wire-order and T-SQL `FORMAT()` stories, are in
[docs/process.md](docs/process.md).

## Request and data flow

### One OIDC login, end to end (portal, Okta mode)

1. The user hits a protected portal page; the cookie scheme redirects to `/Login`
   (`Pages/Login.cshtml.cs`).
2. `LoginRouteSelector` reads the portal's TrustMode from `idn.MigrationApps`; Okta mode
   challenges the OIDC handler, Dual shows a chooser, Adfs redirects to adfs-sim
   (`Auth/LoginRoute.cs`).
3. The browser lands on okta-sim `/authorize` with `response_type=code`; the endpoint
   validates the registered `redirect_uri`, scope, and (for public clients) the S256
   `code_challenge` before anything else (`Endpoints/Oidc.cs`).
4. After the demo login form (or a `login_hint` short-circuit), a single-use
   five-minute authorization code is stored with the challenge and nonce
   (`Services/AuthCodeStore.cs`) and redirected to `http://localhost:5200/signin-oidc`.
5. The portal's OIDC handler exchanges code plus client credentials (Basic auth) at
   `/token`; the server atomically consumes the code, and mints an RS256 access token
   (15 min), an ID token with nonce (60 min), and a refresh token
   (`Services/TokenService.cs`, `Services/RefreshTokenStore.cs`).
6. `OnTokenValidated` folds the claims into the portal principal shape via
   `PortalClaims.Transform`, and the cookie is issued (`Program.cs` in
   `src/Corridor.Portal`).

### One SOAP call with its identity header (portal bridge to TraceLink)

1. A signed-in user opens Cases; `/api/cases` checks the `AnyRole` policy and reads the
   caller's upn (`Api/CasesApi.cs`).
2. `LegacyCredentialFactory` reads the legacy app's TrustMode: Adfs mints a signed
   service SAML assertion with the ADFS dev certificate; Dual and Okta fetch a
   client-credentials JWT from okta-sim (cached ten minutes)
   (`Services/TraceLink/SoapTraceLinkClient.cs`).
3. The client builds a SOAP 1.1 envelope with `<cor:Security>` in
   `http://corridor.example/security` carrying the assertion or the `jwt` element,
   forwards the inbound `traceparent`, and posts to `/TraceLink.svc`
   (`SoapTraceLinkClient.CallAsync`).
4. On the service, `CorridorSecurityMessageInspector` finds the header, extracts the
   token, and `TokenValidator` gates the token kind against `idn.MigrationApps` (wrong
   kind for the mode is `cor:InvalidIdentityMode`) before the matching strategy validates
   signature, audience, and lifetime (`Security/TokenValidator.cs`,
   `Security/JwtTokenValidator.cs`, `Security/SamlTokenValidator.cs`).
5. `TraceLinkService` runs the operation through raw ADO.NET stored procedures; SQL
   errors (including illegal transitions) surface as `cor:` faults, which the bridge maps
   to RFC 9457 problem details with a `faultSubcode` extension (`Services/TraceLinkService.cs`,
   `DataAccess/SqlFaultMapper.cs`).

### The cutover flip sequence

![Cutover sequence: the three applications move from TrustMode Adfs (SAML everywhere, baseline regression pass) through Dual (SAML or JWT accepted, both providers verified live) to Okta (OIDC and JWT only, rollback is a flip back), with shared state in idn.MigrationApps plus an audit row per flip, and a Postman, SoapUI, and JMeter regression gate run between every phase](docs/diagrams/cutover.svg)

1. Baseline: all apps in Adfs mode; the regression suite records a green run.
2. An admin flips one app to Dual from the dashboard; `TrustModeService.FlipAsync`
   updates `idn.MigrationApps` and writes a `TrustModeChanged` audit row in one operation.
3. Both provider paths are exercised live; the regression suite re-runs against the same
   contracts in the new mode.
4. Green: flip to Okta. Anything regressed: flip back (Dual -> Adfs, or Okta -> Adfs),
   also just a flip plus an audit row. Full procedure: [docs/runbook.md](docs/runbook.md).

## Stack, and why

| Area | Choice and why |
|---|---|
| **.NET 10 / C# (latest), solution `Corridor.slnx`** | One toolchain for the whole estate; `Directory.Build.props` sets `net10.0`, nullable, and `TreatWarningsAsErrors` so the baseline is strict |
| **CoreWCF for TraceLink** | Real SOAP 1.1 + WSDL on modern ASP.NET Core hosting; the dispatch-inspector extension point is where the dual-token profile lives ([ADR 0004](docs/adr/0004-token-header-for-soap.md)) |
| **Raw ADO.NET (no ORM) in the legacy service** | The point of the demo: the data layer crosses the migration untouched ([ADR 0005](docs/adr/0005-keep-raw-adonet.md)) |
| **ASP.NET Core OIDC/JwtBearer handlers in the portal** | Framework-standard client plumbing against okta-sim's discovery document; a policy scheme lets JSON APIs accept cookie or bearer |
| **oidc-client-ts in the SPA** | Battle-tested browser OIDC with PKCE and silent renew; sessionStorage by deliberate demo choice (`src/auth/userManager.ts`) |
| **SQL Server via azure-sql-edge (compose)** | arm64-native container for the local stack; procs and schemas as reviewable idempotent files under `db/sql/` |
| **Serilog + OpenTelemetry traceparent** | Console logging everywhere; the portal forwards `traceparent` on the SOAP hop and logs the correlation id ([docs/runbook.md](docs/runbook.md)) |
| **xUnit, Testcontainers, Playwright, Vitest** | Unit per service; integration boots the real stack; e2e drives the three login flows and the flip; see Testing below |
| **VB.NET ops tool** | Small, boring, scriptable operator console in the language this tool class grew up in ([ADR 0009](docs/adr/0009-vb-ops-tool.md)) |

## Testing

| Tier | Count / scope | What it protects |
|---|---|---|
| OktaSim unit (`tests/Corridor.OktaSim.Tests`) | 31 tests | OIDC + PKCE paths, JWKS, SCIM, SAML mode, XACML decisions, admin and health |
| AdfsSim unit (`tests/Corridor.AdfsSim.Tests`) | 22 tests | AuthnRequest parsing, federation metadata, response building and validation, login flow |
| Legacy unit (`tests/Corridor.Legacy.Tests`) | 40 tests | Contract shape, both token validators, mode gating, the security inspector, SQL fault mapping |
| Portal unit (`tests/Corridor.Portal.Tests`) | 46 tests | ACS endpoint, login routing, claims transformation, problem-details mapping, TrustMode service, checklist logic |
| Ops.Tool unit (`tests/Corridor.Ops.Tool.Tests`) | 60 tests | The VB command surface: decoding, validation, metadata parsing, tables |
| SPA unit (`src/Corridor.Spa/src`, Vitest) | 38 tests | Checklist reducer, token-claim display, API client and problem handling, routing |
| Integration (`tests/Corridor.IntegrationTests`) | Testcontainers SQL + real child processes on contract ports | OIDC end to end, SAML end to end, direct SOAP, the portal bridge, SCIM provisioning, XACML, the admin flip, status transitions, service health |
| E2E (`e2e/`) | Self-bootstrapping Playwright suite | The three login flows and the cutover toggle in a real browser; boots its own stack |
| API regression | Postman + SoapUI + JMeter artifacts | Independent, per-phase verification outside this codebase ([ADR 0008](docs/adr/0008-regression-toolchain.md)) |

Gate details, the identity-mode matrix (3 modes x 3 apps), and exit criteria:
[docs/test-plan.md](docs/test-plan.md). CI runs the container-free tiers on every push and
pull request (`.github/workflows/ci.yml`).

## Security and operations

- Demo secrets are named, trivial, and scoped: `corridor-portal-secret`,
  `corridor-legacy-secret`, `corridor-scim-token`, SQL `sa` password `CorridorDev1!`, and
  committed dev certificates under `certs/` that protect nothing. The honest split
  between demo-grade choices and production-hardened patterns is written down in
  [docs/security.md](docs/security.md).
- Production patterns that are real here: PKCE S256, signed XML with DTD-prohibited
  parsing (`Saml/SafeXml.cs`), single-use authorization codes, refresh-token rotation
  with family revocation, fixed-time comparisons, stored-proc transition guards, and audit
  events on every flip.
- Findings from static and web scanning live in
  [docs/security-findings-log.md](docs/security-findings-log.md); scanning configuration
  is `sonar-project.properties` (scanning runs outside CI by choice).
- Operating the stack, flipping TrustMode three ways (dashboard, SQL fallback, audit
  verification), token troubleshooting with the ops tool, key rotation, swap-to-real
  pointers, and database backup: [docs/runbook.md](docs/runbook.md).

## Jargon

Terms from identity migration (cutover, dual trust, provisioning) through protocol
vocabulary (SAML assertion, PKCE, JWKS, SCIM, XACML) to repo-specific state (`TrustMode`)
are defined in the [glossary](docs/GLOSSARY.md), plain English first. Two terms this page
introduces beyond the glossary: **contract ports** (the fixed localhost ports 8080, 8090,
8000, 5200, 5173 that the sims, services, and tests all agree on) and **the flip** (one
TrustMode transition, one audit row).
