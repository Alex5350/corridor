# Corridor implementation plan

How the migration program is built, in workstreams, against the actual repo layout. The
companion documents: [migration-plan.md](migration-plan.md) is what happens operationally
(phases, flips, rollback); this is what had to exist first and in what order. Task
breakdowns cite the real files so an engineer can walk the repo and see each workstream's
footprint.

## Environment prerequisites

- .NET 10 SDK (the solution `Corridor.slnx` targets `net10.0` with
  `TreatWarningsAsErrors` per `Directory.Build.props`)
- Docker with compose v2 (SQL Server runs as `azure-sql-edge`; the db-init sidecar runs
  an amd64 image for its sqlcmd, emulated on arm64 Macs while the server stays native)
- Node 22 for the SPA (`src/Corridor.Spa/package.json`)
- Contract ports free: 8080, 8090, 8000, 5200, 1433, 5173. On macOS, port 5000 is
  occupied by the AirPlay Receiver, which is why the portal is on 5200
- Optional for the regression tier: Postman, SoapUI 5.x, JMeter

## Workstreams and sequencing

Dependency order matters: the providers must exist before any application can be migrated,
the database before the providers, and the state machine before any flip logic.

```text
WS1 database foundation
   -> WS2 legacy provider (adfs-sim)
   -> WS3 target provider (okta-sim)
        -> WS4 SOAP identity swap (TraceLink)
        -> WS5 portal migration + bridge
             -> WS6 SPA (post-cutover client)
WS7 ops tool (parallel, needs WS2/WS3 endpoints)
WS8 test and regression tiers (grows with every workstream)
WS9 documentation (this suite; continuous)
```

## Workstream breakdown

### WS1: database foundation

| Task | Where it landed |
|---|---|
| Schemas and tables: `perm`, `trace`, `idn` | `db/sql/001_schemas.sql` |
| Trace procedures with transition guards (`usp_UpdateStatus` raises state 40001) | `db/sql/002_trace_procs.sql` |
| Synthetic seed: 4 users, 3 MigrationApps rows (all Adfs), 12 trace cases, 8 permits, 6 assignments | `db/sql/seed/003_seed.sql` |
| Container wiring: healthcheck, ordered idempotent sqlcmd init | `docker-compose.yml` (`db`, `db-init`), `scripts/dev-up.sh` |

**Definition of done:** scripts apply cleanly in order on a fresh container, re-run
without error (idempotent), and the transition guard rejects an illegal move (Received ->
Closed) with error state 40001. This is where the T-SQL `FORMAT()` story in
[process.md](process.md) was caught: it works on SQL Server 2022 and fails on
azure-sql-edge, so "works on my SQL Server" was not done.

### WS2: legacy provider (adfs-sim)

| Task | Where it landed |
|---|---|
| Federation metadata (EntityDescriptor, cert, POST binding) | `src/Corridor.AdfsSim/Saml/FederationMetadata.cs` |
| AuthnRequest parsing (redirect and POST bindings) | `src/Corridor.AdfsSim/Saml/AuthnRequestParser.cs` |
| Signed SAML 2.0 responses with upn/role claims, lifetimes, skew | `src/Corridor.AdfsSim/Saml/SamlResponseBuilder.cs`, `SamlSigner.cs` |
| Login page and SSO endpoint, relying-party allowlist | `src/Corridor.AdfsSim/Pages/` (Ls.cshtml.cs, _LoginForm.cshtml), `RelyingPartyRegistry.cs` |
| Dev certificate, committed deliberately as synthetic material | `certs/adfs-sim-cert.pem`, `certs/README.md` |

**Definition of done:** metadata fetches clean through `corridor-ops check-metadata --idp
adfs`, and a relying party completes a full SP-initiated POST login.

### WS3: target provider (okta-sim)

| Task | Where it landed |
|---|---|
| OIDC discovery, authorize (PKCE enforced), token (code, refresh, client credentials), userinfo, logout | `src/Corridor.OktaSim/Endpoints/Oidc.cs` |
| Client registry: portal (confidential), spa (public, PKCE), legacy (client credentials) | `src/Corridor.OktaSim/Models/OAuthClient.cs` |
| RS256 signing, JWKS with current + retired kid | `src/Corridor.OktaSim/Services/SigningKeys.cs`, `TokenService.cs` |
| Single-use auth codes; refresh rotation with family revocation | `src/Corridor.OktaSim/Services/AuthCodeStore.cs`, `RefreshTokenStore.cs` |
| SAML IdP mode for the portal's dual window | `src/Corridor.OktaSim/Endpoints/Saml.cs`, `Saml/SamlResponseBuilder.cs` |
| SCIM 2.0 provisioning surface | `src/Corridor.OktaSim/Endpoints/Scim.cs` |
| XACML PDP over `policies/*.xacml.xml`, deny-on-error | `src/Corridor.OktaSim/Xacml/PdpEngine.cs`, `src/Corridor.OktaSim/Endpoints/Xacml.cs` |
| Admin persona UI (read-only) | `src/Corridor.OktaSim/Endpoints/Admin.cs` |

**Definition of done:** all three clients complete their flows in the integration suite;
refresh reuse revokes the family; the PDP returns a real XACML Deny with a reason for a
malformed request, never a naked 500.

### WS4: SOAP identity swap (TraceLink)

| Task | Where it landed |
|---|---|
| CoreWCF host, basicHttpBinding, WSDL at `?wsdl` | `src/Corridor.Legacy/Program.cs`, `Contracts/ITraceLinkService.cs` |
| Dispatch inspector reading `cor:Security` (either token) | `src/Corridor.Legacy/Security/CorridorSecurityMessageInspector.cs` |
| Mode gating + two validation strategies (SAML cert, JWT via JWKS) | `src/Corridor.Legacy/Security/TokenValidator.cs`, `SamlTokenValidator.cs`, `JwtTokenValidator.cs`, `CachedJwksProvider.cs` |
| Raw ADO.NET repository and fault mapping | `src/Corridor.Legacy/DataAccess/TraceCaseRepository.cs`, `SqlFaultMapper.cs` |
| Faults with `cor:` subcodes incl. `cor:InvalidIdentityMode` | `src/Corridor.Legacy/Security/CorridorFault.cs` |

**Definition of done:** all four operations pass in all three TrustModes; the wrong token
kind for the mode faults with `cor:InvalidIdentityMode`; the data layer is untouched
relative to WS1 ([ADR 0005](adr/0005-keep-raw-adonet.md)).

### WS5: portal migration and REST bridge

| Task | Where it landed |
|---|---|
| OIDC confidential client + bearer for APIs | `src/Corridor.Portal/Program.cs` (AddOpenIdConnect, AddJwtBearer, ApiOrSpa selector) |
| SAML ACS honoring TrustMode, either IdP's cert | `src/Corridor.Portal/Api/SamlAcsEndpoint.cs`, `Auth/Saml/` |
| Login routing: redirect / challenge / chooser | `src/Corridor.Portal/Auth/LoginRoute.cs`, `Pages/Login.cshtml.cs` |
| TrustMode state machine + audit + dashboard | `src/Corridor.Portal/Services/TrustModeService.cs`, `Pages/Admin/Migration.cshtml.cs`, `Pages/Admin/Audit.cshtml` |
| REST-to-SOAP bridge with RFC 9457 errors and traceparent forwarding | `src/Corridor.Portal/Api/CasesApi.cs`, `Services/TraceLink/SoapTraceLinkClient.cs` |
| Permits pages, assignments API for the SPA | `src/Corridor.Portal/Pages/Permits/`, `Api/AssignmentsApi.cs` |

**Definition of done:** sign-in works through every mode; a flip is observable live;
bridge errors carry `faultSubcode`; both DataContract wire-order stories in
[process.md](process.md) are closed (alphabetical member emission in
`CreateTraceRequestAsync`).

### WS6: SPA (FieldInsight)

| Task | Where it landed |
|---|---|
| Public client with PKCE, silent renew, sessionStorage | `src/Corridor.Spa/src/auth/userManager.ts`, `src/config.ts` |
| Assignments list and detail with checklist toggles | `src/Corridor.Spa/src/views/AssignmentsView.tsx`, `AssignmentDetailView.tsx`, `src/domain/assignments.ts` |
| Token claims display, problem handling | `src/Corridor.Spa/src/components/TokenClaims.tsx`, `src/api/problem.ts` |

**Definition of done:** the inspector demo flow completes against a stack where only
okta-sim is reachable for sign-in; Vitest covers the reducer and claim display
([ADR 0003](adr/0003-oidc-pkce-for-spa.md)).

### WS7: ops tool (VB.NET)

| Task | Where it landed |
|---|---|
| Commands: check-metadata, decode-token, validate-token, whoami-token, scim-dump | `src/Corridor.Ops.Tool/Commands.vb`, `MetadataParser.vb`, `TokenDecoder.vb`, `TokenValidator.vb`, `ScimDump.vb` |
| Exit codes, usage text, table rendering | `src/Corridor.Ops.Tool/ExitCodes.vb`, `HelpText.vb`, `TextTable.vb`, `USAGE.md` |

**Definition of done:** every command scriptable with a deterministic exit code; the
base64url decoder handles `-` and `_` correctly (the war story in [process.md](process.md)
lives here); 60 unit tests green ([ADR 0009](adr/0009-vb-ops-tool.md)).

### WS8: test and regression tiers

| Task | Where it landed |
|---|---|
| Unit suites per service | `tests/Corridor.{OktaSim,AdfsSim,Legacy,Portal,Ops.Tool}.Tests/`, SPA Vitest alongside sources |
| Integration: real stack on contract ports, Testcontainers SQL | `tests/Corridor.IntegrationTests/` (`Infrastructure/CorridorStackFixture.cs`) |
| E2E: self-bootstrapping Playwright suite | `e2e/` |
| API regression artifacts + CI parse gate | `postman/`, `soapui/`, `jmeter/`, the `artifacts` job in `.github/workflows/ci.yml` |

**Definition of done:** counts as verified in [test-plan.md](test-plan.md); the
identity-mode matrix (3 modes x 3 apps) executes green; the regression artifacts run from
a clean clone ([ADR 0008](adr/0008-regression-toolchain.md)).

### WS9: documentation

This suite: README, TECHNICAL, glossary, the plans, runbook, security pair, process
narrative, and the nine ADRs. **Definition of done:** every claim cites a real file; the
quickstart works from a clean clone; no em/en dashes anywhere (portfolio convention).

## Sequencing summary

1. WS1 (database) unblocks everything; it is also the cheapest to get wrong silently,
   hence the azure-sql-edge live-verification lesson.
2. WS2 and WS3 in parallel after WS1: the two providers never depend on each other.
3. WS4 before WS5: the portal's bridge needs a stable SOAP identity profile to talk to.
4. WS5 before WS6: the SPA consumes the portal's bearer-only endpoints.
5. WS7 rides alongside WS2/WS3 (it is their first consumer).
6. WS8 grows incrementally: each workstream lands with its tests, and the regression
   artifacts are added as the contracts stabilize.
7. WS9 continuously; finalized last, when there is something true to write.
