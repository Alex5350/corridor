# ADR 0002: dual-trust cutover, one application at a time

Status: Accepted

## Context

The program promise is "no downtime window". A big-bang switch of an identity provider
fails that promise twice: users holding sessions from the old provider get bounced
mid-work, and any defect discovered after the switch forces an emergency reversal.
Agencies also rarely migrate all applications in one move; the trust plumbing has to
support a mixed estate where some apps already accept the new provider and others do not
yet. The obvious alternatives fail differently: config-file modes need a redeploy per
change (an outage by another name), and a global flag moves all three apps at once with
no way to isolate a regression to one of them.

## Decision

Give every application a `TrustMode` state machine stored in SQL, not in configuration:

- `idn.MigrationApps` (created in `db/sql/001_schemas.sql`) holds one row per app:
  `AppKey`, `TrustMode` (Adfs, Dual, Okta), `LastFlippedAt`, `FlippedBy`.
- The legal cycle is Adfs -> Dual -> Okta, and Okta -> Adfs as the rollback path,
  enforced in code by `TrustModeService.NextMode`
  (`src/Corridor.Portal/Services/TrustModeService.cs`). Every flip writes a
  `TrustModeChanged` row to `idn.AuditEvents` in the same operation.
- Consumers read the mode per request: the portal's login route picks SAML redirect,
  OIDC challenge, or a chooser (`src/Corridor.Portal/Auth/LoginRoute.cs`); the SAML ACS
  refuses SAML outright once mode is Okta (`Api/SamlAcsEndpoint.cs`); the legacy SOAP
  service gates token kinds in `TokenValidator.IsTokenKindAllowed`
  (`src/Corridor.Legacy/Security/TokenValidator.cs`).
- The portal's Admin > Migration dashboard flips apps live
  (`src/Corridor.Portal/Pages/Admin/Migration.cshtml.cs`), one app per press.

## Consequences

- The cutover is per-app and reversible: flipping an app to Dual is invisible to users
  (both providers work), and flipping back is one click plus one audit row; a rehearsed
  rollback is a first-class exit criterion in `docs/migration-plan.md`.
- Rollback never requires a redeploy, because the mode lives in the database; the SQL
  fallback (with its mandatory audit row) is documented in `docs/runbook.md`.
- Dual mode is a real security posture, not a demo trick; `docs/test-plan.md` tests all
  three modes against all three apps, including the `cor:InvalidIdentityMode` fault.
- Every token validation path pays one extra lookup on `idn.MigrationApps` per call; the
  trace service already hits SQL for its stored procedures, so the marginal cost is one
  keyed read.
