# Requirements and stakeholders

The Agile artifact for the Corridor program: who it is for, what they asked for in their
words, and where each ask lives in the repo with the test tier that proves it. Stories
follow the repo's plain style: one sentence of want, one of why, acceptance criteria a
non-engineer could verify by watching the demo.

## Personas

| Persona | Who they are | What they care about |
|---|---|---|
| Program PM (Dana, Admin) | Runs the identity modernization program; owns the schedule and the sign-off | No outages, visible progress per application, an audit trail that answers questions |
| ATF-style customer rep (Priya, Officer) | Speaks for the trace-case users; the domain stakeholder | Investigations continue undisturbed; the case list behaves identically before and after |
| App owners | Own PermitPortal, TraceLink, FieldInsight respectively | Their app's behavior is preserved; they are not surprises in someone else's project |
| Service desk lead (Tom, Clerk) | Front line for "I cannot log in" | Fewer password surfaces, clear error messages, an escalation path that is not "page engineering" |
| Engineers | Build and operate the services | Reversible changes, real protocols to test against, one rulebook for authorization |

(The personas map to the four seeded users in `db/sql/seed/003_seed.sql`: admin@,
officer@, clerk@, inspector@, password `Demo1234!` for all.)

## User stories and acceptance criteria

### US-1: one login across the three applications

As a service desk lead I want one login across the three applications so that users have
a single answer to "which password is this" and our reset queue stops growing.

Acceptance criteria:
- After cutover, the same provider account signs into the portal and the SPA.
- The SOAP service accepts tokens minted for that same account's context.
- No application keeps a local password store for end users.

### US-2: no downtime window

As a program PM I want each application moved without a downtime window so that no user
work is interrupted and no outage approval is needed.

Acceptance criteria:
- Every trust change is a live state change on a running system; no service restart is
  part of a flip.
- During the dual window, both sign-in paths work for the portal.
- A user mid-session when an app flips is not logged out.

### US-3: every trust change audited

As a program PM I want every trust change recorded so that I can answer "who changed what
and when" without an archaeology project.

Acceptance criteria:
- Each flip writes one audit row (actor, app, event, detail) at flip time.
- The audit trail is visible from the portal's Admin pages.
- The audit write and the mode write happen together or not at all.

### US-4: the portal keeps working through the transition

As the PermitPortal owner I want my sign-in page to keep working during the transition so
that I do not have to pick a date that annoys my users.

Acceptance criteria:
- In Adfs mode the old redirect flow works unmodified.
- In Dual mode a chooser offers both providers and both complete sign-in.
- In Okta mode the old path is refused with a clear message, and the new path is the
  default.

### US-5: trace cases behave identically

As the customer rep for trace users I want trace cases to keep working exactly as before
so that ongoing work is not disturbed.

Acceptance criteria:
- Search, get, create, and update return the same results before and after cutover.
- Illegal status changes are refused the same way (error state 40001, `cor:` fault).
- No change to the `trace` schema or its procedures is part of the migration.

### US-6: one-click rollback per application

As an operator I want one-click rollback per application so that a bad flip is reversed
in seconds without a release.

Acceptance criteria:
- Rolling back is a single dashboard action per app, producing an audit row.
- After rollback, the previous provider's sign-in works immediately, no redeploy.
- Rollback works from Okta and from Dual.

### US-7: an operator console for token questions

As an operator I want a console tool that decodes and validates tokens so that I can
triage a login failure myself instead of escalating to engineering.

Acceptance criteria:
- Decoding shows header and payload claims with local-time expiry warnings.
- Validation checks RS256 signature, issuer, audience, expiry, not-before, and says which
  check skipped when no expectation was given.
- Every command has a deterministic exit code scripts can branch on.
- The tool never prints the SCIM bearer token it sends.

### US-8: the SOAP contract is untouched

As the TraceLink owner I want the WSDL contract untouched so that external callers and
WSDL consumers are unaffected by the identity change.

Acceptance criteria:
- Namespace, operations, and binding are identical before and after cutover.
- The identity change is confined to the security header's content.
- A caller sending the pre-migration token kind continues to work until the app flips
  past Dual; after that it receives a named fault, not a generic failure.

### US-9: authorization rules in one place

As an engineer I want authorization decisions centralized so that a role change is one
policy file edit, tested once.

Acceptance criteria:
- Who may read trace cases and who may write assignments is decided by policy files.
- Anything not explicitly permitted is denied.
- A policy error yields a Deny with a readable reason, never a crash.

### US-10: field sessions die with the tab

As an inspector I want the field app to sign me in securely on a shared device so that my
session is gone when I close the tab.

Acceptance criteria:
- The SPA uses the proof-based browser flow (no secret in the bundle).
- The signed-in state lives in per-tab storage and does not survive the tab closing.
- Token renewal happens silently while the tab is open.

## Story-to-implementation traceability

| Story | Code area | Proven by |
|---|---|---|
| US-1 | `src/Corridor.OktaSim/` (OIDC, SCIM), `db/sql/seed/003_seed.sql` (shared users) | Integration: `OidcEndToEndTests`, `ScimProvisioningTests`; e2e |
| US-2 | `src/Corridor.Portal/Services/TrustModeService.cs`, `db/sql/001_schemas.sql` (`idn.MigrationApps`) | Integration: `AdminTrustModeFlipTests`; e2e cutover toggle |
| US-3 | `TrustModeService.FlipAsync` (mode + audit together), `Pages/Admin/Audit.cshtml` | Unit: `TrustModeServiceTests`; integration flip test |
| US-4 | `src/Corridor.Portal/Auth/LoginRoute.cs`, `Pages/Login.cshtml.cs`, `Api/SamlAcsEndpoint.cs` | Unit: `LoginRouteTests`, `AcsEndpointTests`; integration: `SamlEndToEndTests` |
| US-5 | `src/Corridor.Legacy/Services/TraceLinkService.cs`, `DataAccess/TraceCaseRepository.cs`, `db/sql/002_trace_procs.sql` | Unit: `TraceLinkServiceFaultTests`; integration: `StatusTransitionTests`, `DirectSoapTests` |
| US-6 | `TrustModeService.NextMode` (Okta -> Adfs path), `Pages/Admin/Migration.cshtml.cs` | Unit: `TrustModeServiceTests`; integration: `AdminTrustModeFlipTests`; runbook rehearsal |
| US-7 | `src/Corridor.Ops.Tool/` (`Commands.vb`, `TokenDecoder.vb`, `TokenValidator.vb`, `USAGE.md`) | Unit: 60 tests in `tests/Corridor.Ops.Tool.Tests/` |
| US-8 | `src/Corridor.Legacy/Program.cs` (WSDL, binding), `Contracts/ITraceLinkService.cs`, `Security/CorridorSecurityMessageInspector.cs` | Unit: `ContractShapeTests`, `SecurityHeaderInspectorTests`; integration: `DirectSoapTests`; SoapUI project |
| US-9 | `src/Corridor.OktaSim/Xacml/PdpEngine.cs`, `policies/*.xacml.xml` | Unit: `XacmlTests`; integration: `XacmlDecisionTests` |
| US-10 | `src/Corridor.Spa/src/auth/userManager.ts` (PKCE, sessionStorage), `src/config.ts` | SPA Vitest (38 tests: auth context, routing, claims); e2e |

Mapping note: the personas' names are the seeded synthetic users; the demo script in
[onboarding.md](onboarding.md) walks US-1 through US-6 in one pass.
