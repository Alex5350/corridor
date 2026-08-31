# Corridor migration plan: ADFS to Okta-style login, no downtime window

This is the plan the repo exists to demonstrate. It covers the three applications end to
end: what moves, in what order, with what evidence, and how each step reverses. The
engineering rationale behind the design lives in [TECHNICAL.md](../TECHNICAL.md) and the
[ADRs](adr/); this document is the executable program view. Everything here is grounded
in the running system: the flip mechanism is `TrustModeService`
(`src/Corridor.Portal/Services/TrustModeService.cs`), the state is `idn.MigrationApps`
(`db/sql/001_schemas.sql`), and the per-phase evidence is the regression toolchain
([ADR 0008](adr/0008-regression-toolchain.md)).

## Scope

Three synthetic applications, all currently trusting adfs-sim (SAML 2.0) only:

| App | Repo | What it is | Why it is in scope |
|---|---|---|---|
| PermitPortal | `src/Corridor.Portal` | The web application (permits, cases, admin) | Highest visibility; hosts the migration dashboard itself |
| TraceLink | `src/Corridor.Legacy` | The SOAP case service behind the portal bridge | Riskiest asset: CoreWCF, WSDL-first, raw ADO.NET; cannot be rewritten |
| FieldInsight | `src/Corridor.Spa` | The inspector single-page app | Only app that is born post-cutover; must not inherit legacy patterns |

Out of scope: any change to the `trace` and `perm` schemas' existing objects
([ADR 0005](adr/0005-keep-raw-adonet.md)), any rewrite of the SOAP contract
([ADR 0004](adr/0004-token-header-for-soap.md)), and any real provider tenancy
([ADR 0001](adr/0001-simulate-both-providers-locally.md); swap-to-real pointers are in
[runbook.md](runbook.md)).

## Per-application approach

| App | Approach | Why this and not something else |
|---|---|---|
| PermitPortal | OIDC-first with SAML dual-trust: add the OIDC confidential client and the ACS that accepts either IdP, then cross | It is a web app we control end to end; it also carries the chooser UI that makes the dual window observable in demos |
| TraceLink | Token header swap: keep the WSDL, extend the security header to accept SAML or JWT, gate by TrustMode | Its callers cannot change on this schedule; the identity must move without the contract moving |
| FieldInsight | PKCE-only, built post-cutover | A new browser client should never start life with a legacy trust dependency; it exists to show the target state ([ADR 0003](adr/0003-oidc-pkce-for-spa.md)) |

## Phases

Every phase has the same shape: entry criteria, actions, exit criteria, rollback. The
TrustMode cycle is Adfs -> Dual -> Okta, with Okta -> Adfs as the rollback path
(`TrustModeService.NextMode`). Rollback is always "flip back plus an audit row"; it never
requires a redeploy.

| Phase | State | Entry criteria | Key actions | Exit criteria | Rollback |
|---|---|---|---|---|---|
| P0: baseline | All apps Adfs | Stack healthy (`/healthz` on 8080, 8090, 8000, 5200); seeded users sign in via adfs-sim | Run the full regression suite; capture the green baseline; snapshot the db volume (runbook backup step) | Baseline green and archived | n/a (nothing changed) |
| P1: provision | All apps Adfs | P0 exit | Populate the target directory via SCIM (`/scim/v2/Users`): the four seeded users with roles; verify with `corridor-ops scim-dump`; first XACML decision checks against `policies/` | Every baseline user exists in the target provider, active, correct role | Delete the SCIM-created entries (deactivate via PATCH); no app behavior has changed |
| P2: portal to Dual | portal Dual, others Adfs | P1 exit; portal chooser ready | Flip portal from the Admin > Migration dashboard; exercise sign-in both ways; re-run the regression suite in Dual mode | Both provider paths sign in and pass regression; audit shows `TrustModeChanged` for portal | Flip portal back to Adfs (one flip); users keep signing in exactly as before |
| P3: portal to Okta | portal Okta, others Adfs | P2 exit green | Flip portal; confirm SAML ACS now refuses with the "use Okta" message; re-run regression | Portal green on OIDC only for a full pass | Flip portal back to Dual (SAML works again immediately) |
| P4: TraceLink to Dual | legacy Dual | P3 exit; ops tool `check-metadata` and `validate-token` clean for both providers | Flip legacy; exercise the bridge with both token kinds (SoapUI carries SAML and JWT variants); re-run regression including direct SOAP calls | SOAP calls green with either token; `cor:InvalidIdentityMode` correctly absent for both kinds | Flip legacy back to Adfs; SAML-carrying callers are unaffected throughout |
| P5: TraceLink to Okta | legacy Okta | P4 exit green | Flip legacy; verify wrong-mode (SAML) calls fault with `cor:InvalidIdentityMode`; re-run regression | Bridge and direct SOAP green on JWT only | Flip legacy back to Dual (both kinds accepted again) |
| P6: FieldInsight launch | spa Okta (bearer only) | P5 exit | The SPA goes live against okta-sim with PKCE; its `/api/assignments` endpoints accept bearer tokens only | Inspectors complete the assignments demo flow; regression green | The SPA simply is not published (pre-cutover state); no flip involved |
| P7: closeout | All Okta | P6 exit | Final full regression; audit trail review (every flip has an actor and a timestamp); decommission adfs-sim trust in the story sense (the sim stays runnable for demos) | Sign-off checklist below signed | Per-app flips back to Dual remain available indefinitely |

## Communications and occupancy notes

- **User notice before first flip (P2), in brief:** "Starting [date], the portal sign-in
  page will offer a choice of login method during a transition period. Nothing else
  changes; both methods work; if one fails, use the other and tell the service desk."
- **User notice at closeout (P7), in brief:** "The transition is complete: the old login
  page is retired and all three applications now use the single modern sign-in. Your
  username and password are unchanged."
- **Occupancy:** every phase is a live state change on a running system, so user work
  continues through all of them; the only moment a user could notice anything is seeing
  the chooser during P2-P3, which the pre-flip notice explains. No phase asks users to
  log out.
- **Service desk:** the two fault messages users might relay are handled: "The portal no
  longer accepts ADFS sign-in. Use Okta." (portal ACS after P3) and the SOAP
  `cor:InvalidIdentityMode` fault (a wrong-mode integration after P5, an operator issue,
  not a user issue). Escalation path: [runbook.md](runbook.md) token troubleshooting.

## Risks and mitigations

| Risk | Impact if it fires | Mitigation | Rollback trigger |
|---|---|---|---|
| Token clock skew between hosts | Valid tokens rejected (or stale ones accepted) around flip time | Skew is built in and tested: five minutes on SAML (`SamlTokenValidator`), one minute on JWT (`JwtTokenValidator`); the ops tool prints local-time expiry warnings; sync host clocks before P2 | Sign-in failures cluster around token validation; flip back and resync clocks |
| Signing key rotation mishandled by a relying party | Token validation fails after a key change | okta-sim publishes a current and a retired kid in the JWKS (`SigningKeys`), so rotation handling is exercised continuously, not discovered at cutover; `corridor-ops check-metadata --idp okta` lists kids before every flip | Validation failures that clear when the old kid is re-published |
| SCIM drift between directories | First Okta-mode login fails for a specific user | P1 provisions and verifies before any flip; PATCH (active, groups) fixes small drift mid-window without full re-provisioning | Any first-login failure traced to a missing account: fix via SCIM, no flip needed |
| Wrong-mode integration calls a flipped app | SOAP callers fault with `cor:InvalidIdentityMode` | The fault is deliberate and named; SoapUI's SAML/JWT variants reproduce both kinds before the flip; the phase gate catches untested callers | Faults persisting after the caller is corrected: flip back to Dual |
| Stored-proc behavior change sneaks in mid-migration | Trace decisions change for non-identity reasons | The data layer is untouched by policy ([ADR 0005](adr/0005-keep-raw-adonet.md)); the regression suite exercises all four operations each phase | Any trace-operation failure not explained by identity work |
| Migration state inconsistency | Apps disagree about who trusts whom | One row per app in `idn.MigrationApps`, one flip at a time, audit row per flip; the SQL fallback procedure in [runbook.md](runbook.md) includes an audit write | Rows disagreeing with observed behavior: stop, verify with the audit query, correct via the documented procedure |

## Sign-off checklist

Program and engineering sign each phase; the final column is where the evidence lives.

- [ ] Baseline regression run green and archived (P0) - Postman/SoapUI/JMeter results
- [ ] Directory verified complete and correct via `scim-dump` (P1)
- [ ] Portal Dual flip audited, both sign-in paths demonstrated, regression green (P2)
- [ ] Portal Okta flip audited, old path cleanly refused, regression green (P3)
- [ ] TraceLink Dual flip audited, both token kinds demonstrated, regression green (P4)
- [ ] TraceLink Okta flip audited, wrong-mode fault verified, regression green (P5)
- [ ] FieldInsight live on PKCE, assignments flow complete (P6)
- [ ] Full regression green at closeout; audit trail reviewed flip by flip (P7)
- [ ] Rollback path rehearsed once in a non-production environment before P2
- [ ] User notices sent per the communications section
