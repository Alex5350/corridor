# Corridor onboarding: day one

Everything needed to go from a clean clone to flipping TrustMode yourself. Long-form
reference: [runbook.md](runbook.md) for operations, [TECHNICAL.md](../TECHNICAL.md) for
architecture. Jargon: [GLOSSARY.md](GLOSSARY.md).

## Prerequisites

- Docker with compose v2
- .NET 10 SDK
- Node 22 (only if you will touch the SPA)
- Free contract ports: 8080, 8090, 8000, 5200, 1433, 5173

macOS note: port 5000 is squatted by the AirPlay Receiver (System Settings > General >
AirDrop & Handoff). The portal runs on **5200** for exactly this reason; every Mac
developer hits this once, and the port is now consistent across the portal config
(`src/Corridor.Portal/appsettings.json`, `Portal:BaseUrl`), the okta-sim client registry
redirect, and the integration suite (`CorridorStackFixture.PortalPort`). If a compose
file or script you are given still maps 5000, it predates the move; prefer `dotnet run`
(launchSettings uses 5200) or update the mapping.

## Start the stack

```bash
scripts/dev-up.sh
```

This starts SQL Server, applies `db/sql/001_schemas.sql`, `002_trace_procs.sql`, and
`seed/003_seed.sql` in order (idempotent), builds and starts every service, and waits on
each health endpoint. Stop with `scripts/dev-down.sh`; add `CORRIDOR_PURGE=1` to also
drop the db volume and start from a clean database next time.

| What | Where | Notes |
|---|---|---|
| Portal (PermitPortal) | http://localhost:5200 | Sign in; Admin > Migration is the dashboard |
| okta-sim admin console | http://localhost:8080 | Read-only persona UI |
| adfs-sim login page | http://localhost:8090 | Appears inside portal logins in Adfs mode |
| TraceLink WSDL | http://localhost:8000/TraceLink.svc?wsdl | Feed this to SoapUI |
| FieldInsight SPA | http://localhost:5173 | Inspector app |

Demo logins (synthetic, password `Demo1234!` for all): `admin@corridor.example` (Admin),
`inspector@corridor.example` (Inspector), `officer@corridor.example` (Officer),
`clerk@corridor.example` (Clerk). All apps start in Adfs trust mode.

## Repo tour, in dependency order

1. **The contract** (`docs/contracts/ARCHITECTURE-CONTRACT.md`): the source of truth the
   repo was built against; read this first on any question of intent.
2. **The database** (`db/sql/`): schemas, the trace procedures with transition guards,
   the seed. Everything downstream reads these.
3. **The providers**: `src/Corridor.AdfsSim` (SAML 2.0 IdP) and `src/Corridor.OktaSim`
   (OIDC + SAML + SCIM + XACML). They depend only on the database (optionally; both fall
   back to in-memory stores without a connection string).
4. **The legacy service**: `src/Corridor.Legacy`. CoreWCF host in `Program.cs`, the dual
   token profile in `Security/`, raw ADO.NET in `DataAccess/`.
5. **The portal**: `src/Corridor.Portal`. Auth in `Auth/`, the SAML ACS in `Api/`, the
   bridge in `Api/CasesApi.cs` + `Services/TraceLink/`, the dashboard in
   `Pages/Admin/`.
6. **The SPA**: `src/Corridor.Spa`. Config in `src/config.ts`, the OIDC client in
   `src/auth/userManager.ts`.
7. **The ops tool**: `src/Corridor.Ops.Tool` plus its `USAGE.md`.
8. **The tests**: `tests/` per service, `tests/Corridor.IntegrationTests` for the whole
   stack, `e2e/` for the browser flows, and the regression artifacts in `postman/`,
   `soapui/`, `jmeter/`.

Where things live, quick table:

| I need to change... | Start in |
|---|---|
| A login flow detail | `src/Corridor.Portal/Auth/` or `src/Corridor.OktaSim/Endpoints/Oidc.cs` |
| A trace rule (status transitions) | `db/sql/002_trace_procs.sql` (rules live in SQL, [ADR 0005](adr/0005-keep-raw-adonet.md)) |
| Who may do what | `policies/*.xacml.xml` ([ADR 0007](adr/0007-xacml-central-pdp.md)) |
| Seed data | `db/sql/seed/003_seed.sql` |
| The SOAP identity profile | `src/Corridor.Legacy/Security/` |
| Demo secrets | appsettings and compose; all named and documented in [security.md](security.md) |

## Run each test tier

```bash
# Unit: everything except the integration suite (what CI's build-test job runs)
dotnet test Corridor.slnx --filter "FullyQualifiedName!~IntegrationTests"

# Integration: boots Testcontainers SQL plus real service processes on contract ports
dotnet test tests/Corridor.IntegrationTests/Corridor.IntegrationTests.csproj

# SPA
cd src/Corridor.Spa && npm ci && npm test && npm run lint && npm run build

# E2E: self-bootstrapping Playwright suite (creates the db if missing, boots the stack)
# see e2e/ ; drives the three login flows and the cutover toggle

# API regression (against a running stack)
jmeter -n -t jmeter/corridor-flow.jmx -l results.jtl   # plus Postman/SoapUI from their apps
```

Counts and gates: [test-plan.md](test-plan.md).

## Flip TrustMode from the admin dashboard

1. Sign in to the portal as `admin@corridor.example` (in Adfs mode you will pass through
   adfs-sim's login page; in Dual you get a chooser).
2. Open Admin > Migration.
3. Each app row shows its current mode (Adfs, Dual, or Okta), what the next flip gives
   you, and the last flip's actor and time. Press the flip button for one app.
4. Verify: the page updates, and Admin > Audit shows a `TrustModeChanged` row naming you.
5. Feel it: in a private window, sign in to the portal again and observe the changed
   behavior (chooser in Dual; okta-sim's sign-in form in Okta).

The cycle is Adfs -> Dual -> Okta -> Adfs, so you can always flip back the way you came
(or all the way back from Okta in one press). The same page carries the account side of
the cutover: the **Directory provisioning** card's **Provision directory** button pushes
`idn.Users` into okta-sim over SCIM and records a `DirectoryProvisioned` audit row (demo
script step 2 below). The SQL fallback and audit verification procedure:
[runbook.md](runbook.md).

## The demo script, in brief

1. **Baseline**: with everything in Adfs mode, sign in to the portal via adfs-sim; show
   Permits and Cases (the bridge calling the SOAP service under SAML).
2. **Provision**: sign in as the admin, open Admin > Migration, and press
   **Provision directory**: the dashboard reports the run's created/updated/deactivated
   counts, Admin > Audit shows the `DirectoryProvisioned` row, and the okta-sim admin
   console (or `corridor-ops scim-dump --url http://localhost:8080 --token
   corridor-scim-token`) lists the same accounts, now SCIM-managed.
3. **Dual**: flip the portal; sign out; land on the chooser; sign in both ways.
4. **Okta**: flip again; show the old path refused with a clear message; show
   `corridor-ops decode-token` on a fresh token (role, upn, expiry).
5. **SOAP crosses too**: flip TraceLink through Dual to Okta; open Cases; explain the
   bridge now carries a JWT in the same header; optionally show the SoapUI JWT variant.
6. **SPA**: open http://localhost:5173, sign in as the inspector, toggle a checklist
   item; point at the profile card's raw claims.
7. **Rollback**: flip anything back; show the audit trail.
