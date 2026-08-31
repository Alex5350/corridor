# ADR 0006: SCIM 2.0 as the provisioning bridge

Status: Accepted

## Context

An identity migration that only moves login flows leaves the accounts behind. In the real
version of this program, users already exist in the ADFS-backed directory; after cutover
they must exist in the Okta org with the same identifier, display name, role, and active
state, or every sign-in fails on day one. Doing this by hand does not scale and does not
audit; doing it with a bespoke CSV import is a one-shot script nobody can re-run
mid-cutover when the directory drifts; and a custom REST API would mean inventing (and
documenting, and defending) a schema for something a standard already covers.

## Decision

Implement SCIM 2.0 on the target provider and treat it as the provisioning bridge:

- okta-sim exposes `/scim/v2/Users` with list (filter `userName eq "value"` is the one
  supported form; anything else is a 400 per RFC 7644), create, get, put (replace), and
  patch (replace ops on `active` and `groups` only), with `application/scim+json` bodies
  and RFC 7644 error shapes (`src/Corridor.OktaSim/Endpoints/Scim.cs`).
- The backing store is the same `idn.Users` table the sims validate logins against, so a
  SCIM write is immediately visible to every login path; `ScimExternalId` records the
  bridge's correlation id for the source directory.
- Bearer auth uses the documented demo constant `corridor-scim-token`; the ops tool can
  dump the directory on demand (`corridor-ops scim-dump`, exit 5 on a SCIM error).

## Consequences

- Accounts move with the cutover instead of being re-created: provision the user via
  SCIM before flipping an app, and the first Okta-mode sign-in finds the account already
  there with the right role. In the demo this is visible directly, since the okta-sim
  admin console lists the same store SCIM writes to.
- The same bridge serves both directions of drift during the dual-trust window
  (activate, deactivate, fix groups) because PATCH handles the small cases without full
  replaces.
- Integration coverage exists for the whole bridge
  (`tests/Corridor.IntegrationTests/ScimProvisioningTests.cs`), so provisioning
  regressions surface before a flip, not after; unit coverage pins the filter and patch
  semantics inside the okta-sim suite.
- The demo-grade bearer token is documented as such in `docs/security.md`; the
  swap-to-real pointer (base URL plus a real token via environment) is in
  `docs/runbook.md`.
