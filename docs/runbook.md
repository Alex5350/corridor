# Corridor runbook

Operating the demo estate: health checks, the TrustMode flip in all its forms, token
troubleshooting with the ops tool, key and certificate rotation, swap-to-real pointers
for turning the simulation into a real migration rehearsal, and database backup. For the
program-level flip sequence, see [migration-plan.md](migration-plan.md); for what is
deliberately demo-grade, [security.md](security.md).

## Health endpoints

Every service exposes an anonymous `/healthz` returning `{"status":"ok"}`.

| Service | URL | Backing note |
|---|---|---|
| okta-sim | http://localhost:8080/healthz | Also check discovery: `/.well-known/openid-configuration` |
| adfs-sim | http://localhost:8090/healthz | Metadata: `/federationmetadata/2007-06/federationmetadata.xml` |
| TraceLink | http://localhost:8000/healthz | WSDL: `/TraceLink.svc?wsdl` |
| portal | http://localhost:5200/healthz | Then check the Admin > Migration page loads |
| SPA | http://localhost:5173 | Static container; no healthz, the page is the probe |
| db | compose health via TDS probe | `docker compose ps db`; the compose healthcheck probes port 1433 |

## TrustMode flip procedure

### Preferred: the dashboard

1. Sign in to the portal as an Admin (`admin@corridor.example`).
2. Open Admin > Migration.
3. Confirm the app's current mode and the announced next mode (cycle: Adfs -> Dual ->
   Okta -> Adfs; rollback is a normal flip).
4. Press the flip button for that one app.
5. Verify below (audit verification) before touching the next app.

One app at a time, always. The flip writes `idn.MigrationApps` and an
`idn.AuditEvents` row together (`TrustModeService.FlipAsync`).

### Fallback: SQL

When the dashboard is unavailable but the database and the app are reachable:

```sql
-- Read current state
SELECT AppKey, AppName, TrustMode, LastFlippedAt, FlippedBy FROM idn.MigrationApps;

-- Flip one app (example: legacy Dual -> Okta), then write the matching audit row
UPDATE idn.MigrationApps
   SET TrustMode = N'Okta', LastFlippedAt = SYSUTCDATETIME(), FlippedBy = N'operator@corridor.example'
 WHERE AppKey = N'legacy';

INSERT idn.AuditEvents (Actor, AppKey, Event, Detail)
VALUES (N'operator@corridor.example', N'legacy', N'TrustModeChanged', N'Dual -> Okta');
```

Constraints: keep `TrustMode` to exactly `Adfs`, `Dual`, or `Okta`; do both statements;
never edit history rows.

### Audit verification (after any flip, either path)

```sql
SELECT TOP 5 At, Actor, AppKey, Event, Detail
  FROM idn.AuditEvents
 WHERE Event = N'TrustModeChanged'
 ORDER BY Id DESC;
```

Expect one new row naming the actor and the transition (for example "Dual -> Okta").
Cross-check that `idn.MigrationApps.TrustMode` agrees with the newest row, then exercise
the app's sign-in path for the new mode.

## Token troubleshooting with the ops tool

Full usage: `src/Corridor.Ops.Tool/USAGE.md`. Build once with
`dotnet build src/Corridor.Ops.Tool`.

| Symptom | Command sequence |
|---|---|
| "Is the provider even sane before I flip?" | `dotnet run --project src/Corridor.Ops.Tool -- check-metadata --idp adfs` then `--idp okta` (exit 2 bad metadata, 3 unreachable) |
| Login fails for one user | Get their token from the failing flow, then `... -- decode-token <jwt>`: check `upn`, `role`, and the EXPIRES / NOT-YET-VALID warnings with local times |
| "Is the signature good, or is it the claims?" | `... -- validate-token <jwt> --jwks http://localhost:8080/jwks --iss http://localhost:8080 --aud legacy` (each check passes, fails, or is reported skipped) |
| Who is this token, quickly | `... -- whoami-token <jwt>` |
| Did SCIM carry the account over? | `... -- scim-dump --url http://localhost:8080 --token corridor-scim-token` |

Reading the faults: `cor:MissingSecurityHeader` and `cor:InvalidTokenFormat` mean header
shape (see [process.md](process.md), the namespace war story); `cor:InvalidIdentityMode`
means a right token in the wrong mode (check the table above); `cor:InvalidToken` means
signature, audience, or lifetime (check clocks and keys); `cor:IllegalStatusTransition`
is correct refusal of an illegal case move, not a fault in the system.

Correlation: the portal forwards the inbound `traceparent` on the SOAP hop and logs the
correlation id per call (`SoapTraceLinkClient.CallAsync`); match service desk reports by
time and correlation id in the logs.

## Key and certificate rotation notes (the sims)

- **okta-sim signing key**: the current kid `okta-sim-2026-08` is the committed demo PEM
  (`certs/okta-sim-signing-key.pem`, loaded by `SigningKeys`); a retired kid
  `okta-sim-2026-02` is generated and published so relying parties exercise rotation
  continuously. To rotate for real in a rehearsal: generate a new PEM, mount/configure it
  via `OktaSim__SigningKeyPem`, restart okta-sim, and verify with
  `check-metadata --idp okta` (it lists JWKS kids) plus `validate-token` on a fresh
  token. Relying parties that resolve keys by `kid` (the legacy service's
  `CachedJwksProvider` caches briefly) need nothing else.
- **adfs-sim certificate**: `certs/adfs-sim-cert.pem` with `adfs-sim-key.pem`, referenced
  by `AdfsSim__CertificatePath` / `AdfsSim__KeyPath`. After replacing, consumers must
  trust the new cert: the portal's `Adfs:CertificatePath` and the legacy service's
  `Corridor:Adfs:SigningCertPath` both pin the ADFS signing certificate. Check expiry
  through `check-metadata --idp adfs` (it prints subject, thumbprint, and expiry).
- **okta-sim SAML certificate**: derived in memory from the signing RSA key at startup
  (`SigningKeys.CreateDevelopmentCertificate`), never persisted; rotating the RSA PEM
  rotates it.

## Swap-to-real pointers

The sims implement real protocols, so rehearsal against real providers is configuration,
not code ([ADR 0001](adr/0001-simulate-both-providers-locally.md)).

| What | Where the demo value lives | Point it at the real thing |
|---|---|---|
| Real Okta org | `Okta:Authority` = `http://localhost:8080` (portal `appsettings.json`); client ids `portal`, `spa`, `legacy` with demo secrets | Set the authority to the org URL; register real OIDC applications; supply client ids and secrets via environment (`Okta__ClientSecret`, `Legacy__OktaClientSecret`) or user secrets; PKCE clients need no secret |
| Real ADFS | `Adfs:BaseAddress` = `http://localhost:8090`, metadata at `/federationmetadata/...`, pinned cert paths | Set the real metadata URL and fetch the real signing cert thumbprint; replace the two `certs/` PEMs' role with the real token-signing certificate's public half |
| SCIM | `POST /scim/v2/Users` on okta-sim, bearer `corridor-scim-token` | Point the provisioning side at the real SCIM base URL (for example the org's SCIM connector) with a real token via environment; the ops tool's `--url`/`--token` flags already take it |
| SQL | `ConnectionStrings:Corridor` = localhost,1433 with the demo sa password | Set the real connection string via environment (`ConnectionStrings__Corridor`) or `dotnet user-secrets`; never in appsettings.json |

Client secrets in this repo are named demo constants with no secret value
([security.md](security.md)); anything real arrives via environment or user secrets.

## Backup and restore (db container volume)

The database (schemas, procs, seed, and the live migration state and audit trail) lives
in the named compose volume `corridor-mssql-data`. The azure-sql-edge server image ships
no sqlcmd, so run the tools through a one-off db-init container (the compose service
already pins the right image, network, and platform) with the current directory mounted
at /backup so the file lands on the host:

```bash
# Backup: dump the Corridor database to ./corridor.bak
docker compose --profile full run --rm \
  --entrypoint /opt/mssql-tools18/bin/sqlcmd \
  -v "$PWD:/backup" \
  db-init -S db -U sa -P 'CorridorDev1!' -C \
  -Q "BACKUP DATABASE Corridor TO DISK='/backup/corridor.bak'"

# Restore (example: after scripts/dev-down.sh with CORRIDOR_PURGE=1 and a fresh dev-up)
docker compose --profile full run --rm \
  --entrypoint /opt/mssql-tools18/bin/sqlcmd \
  -v "$PWD:/backup" \
  db-init -S db -U sa -P 'CorridorDev1!' -C \
  -Q "RESTORE DATABASE Corridor FROM DISK='/backup/corridor.bak' WITH REPLACE"
```

For ad hoc SQL, the same one-off container works without the bind mount:

```bash
docker compose --profile full run --rm \
  --entrypoint /opt/mssql-tools18/bin/sqlcmd \
  db-init -S db -U sa -P 'CorridorDev1!' -C \
  -Q "SELECT AppKey, TrustMode, FlippedBy FROM idn.MigrationApps"
```

Take a backup before any flip marathon; the audit trail is only as durable as the volume
it sits on.
