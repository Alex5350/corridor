# Corridor test plan

How the migration earns confidence at every level: unit, integration, end-to-end, and the
committed API regression artifacts run between every migration phase. Counts below are
the real test-case counts in the repo (xUnit `[Fact]`/`[Theory]` cases and Vitest cases),
not estimates.

## Test tiers

| Tier | Where | Count / scope | What it protects |
|---|---|---|---|
| OktaSim unit | `tests/Corridor.OktaSim.Tests/` | 31 tests (flows, discovery/JWKS, SCIM, SAML mode, XACML, admin/health) | The target provider's protocol behavior in isolation |
| AdfsSim unit | `tests/Corridor.AdfsSim.Tests/` | 22 tests (AuthnRequest parsing, metadata, response building, validation, login) | The legacy provider's SAML correctness |
| Legacy unit | `tests/Corridor.Legacy.Tests/` | 40 tests (contract shape, both validators, mode gating, inspector, fault mapping) | The dual-token SOAP profile and the preserved data layer seams |
| Portal unit | `tests/Corridor.Portal.Tests/` | 46 tests (ACS, login routing, claims, problem details, TrustMode, checklists) | The web app's routing and bridge translation |
| Ops.Tool unit | `tests/Corridor.Ops.Tool.Tests/` | 60 tests (decoding, validation, metadata, tables) | The operator's flip-window answers |
| SPA unit | `src/Corridor.Spa/src` (Vitest) | 38 tests (checklist reducer, claims display, API client, problem handling, routing) | The post-cutover client |
| Integration | `tests/Corridor.IntegrationTests/` | Whole-stack suite via `CorridorStackFixture`: Testcontainers SQL plus the four .NET services as real processes on the contract ports | Real protocols over real HTTP against a real database: OIDC end to end, SAML end to end, direct SOAP, the bridge, SCIM provisioning, XACML decisions, the admin flip, status transitions, health |
| E2E | `e2e/` | Self-bootstrapping Playwright suite (creates the db if missing, boots the stack, drives the three login flows and the cutover toggle) | What a user actually experiences, in a browser, per mode |
| API regression | `postman/`, `soapui/`, `jmeter/` | See below | An independent verdict from outside this codebase, per phase |

Unit and integration tests run in CI (`.github/workflows/ci.yml`): the `build-test` job
runs everything except IntegrationTests (container-free, fast), and the `integrate` job
starts the compose db via `scripts/test-db-only.sh` and runs the integration project.

## API regression tier: the committed artifacts

Three artifacts, run between every migration phase ([ADR 0008](adr/0008-regression-toolchain.md)).
CI's `artifacts` job parse-checks their well-formedness on every push; missing files warn,
malformed files fail.

- **Postman**: `postman/Corridor.postman_collection.json`, environment-variable driven.
  Folders: Health, OIDC (the full code + PKCE dance scripted in Postman test scripts),
  SCIM CRUD, XACML decide, Portal REST, and a SoapUI-parity SOAP call. Run from the
  Postman app or newman against a running stack.
- **SoapUI**: `soapui/Corridor-TraceLink-soapui-project.xml`, a real SoapUI 5.x project
  against the live WSDL at `http://localhost:8000/TraceLink.svc?wsdl`: one test request
  per TraceLink operation, SAML and JWT header variants, and a TestSuite asserting SOAP
  Fault behavior, schema conformance, and an XQuery on CaseNumber.
- **JMeter**: `jmeter/corridor-flow.jmx`, two thread groups: "Portal read path" (login ->
  permits -> cases loop) and "SCIM write path", over a CSV of synthetic users, with
  response-code and JSON assertions. Headless run:

```bash
jmeter -n -t jmeter/corridor-flow.jmx -l results.jtl
```

Honesty note: JMeter here verifies that the paths hold and assertions pass under
repetition; the repo makes no load or performance claims.

## Identity-mode test matrix

The core of the plan: every application's acceptance is tested in every TrustMode. Cells
name the behavior that must hold; the integration suite and the SoapUI variants cover the
SOAP column, integration plus e2e cover the interactive columns.

| App \ Mode | Adfs | Dual | Okta |
|---|---|---|---|
| PermitPortal | SAML login via adfs-sim works; OIDC challenge not offered | Chooser shown; both providers sign in; both sessions reach protected pages | OIDC sign-in works; SAML ACS refuses with the "use Okta" message; bearer accepted on APIs |
| TraceLink (SOAP) | SAML assertion accepted; JWT rejected `cor:InvalidIdentityMode` | Both kinds accepted; each validated by its own strategy | JWT accepted; SAML rejected `cor:InvalidIdentityMode`; bridge uses client credentials |
| FieldInsight (SPA) | Served at 5173 in every mode but always offers the Okta login (the SPA has no trust-mode gate: it exists as a post-cutover client, so in Adfs mode its login simply fails) | Not applicable | PKCE login works; refresh rotation works; assignments PATCH works bearer-only |

Wrong-mode behavior is a first-class expectation, not an edge case: the
`cor:InvalidIdentityMode` fault is asserted in `TokenValidatorModeGatingTests` and
exercised live by the SoapUI variants.

## Per-phase regression gates

Bound to the phases in [migration-plan.md](migration-plan.md):

| Gate | When | Must pass before |
|---|---|---|
| G0 baseline | After stack health, before P1 | Anything flips |
| G1 provisioning | After SCIM population | Portal flips (P2) |
| G2 portal Dual | After the P2 flip | Portal flips to Okta (P3) |
| G3 portal Okta | After the P3 flip | TraceLink flips (P4) |
| G4 legacy Dual | After the P4 flip | TraceLink flips to Okta (P5) |
| G5 legacy Okta | After the P5 flip | SPA launch (P6) |
| G6 closeout | After P6, before sign-off | Program closeout (P7) |

A gate passes when: unit suites green, integration suite green in the current modes, e2e
green for the interactive flows, and the three regression artifacts green from a clean
run. A failed gate blocks the next flip; the rollback path is always the previous mode.

## Defect triage

| Signal | Likely area | First tool |
|---|---|---|
| Sign-in loop or immediate rejection | Claims transformation, ACS validation, OIDC callback | Portal logs; `corridor-ops decode-token` on the ID token |
| `cor:MissingSecurityHeader` or `cor:InvalidTokenFormat` | SOAP header shape on the caller side | SoapUI variant vs the inspector's expectations ([process.md](process.md) has the war story) |
| `cor:InvalidIdentityMode` | A wrong-mode call: integration not following the flip | Check `idn.MigrationApps` (runbook audit query), then retest in the right mode |
| `cor:IllegalStatusTransition` (state 40001) | Correct behavior: the proc refused an illegal move | Verify the intended transition is legal per `db/sql/002_trace_procs.sql` |
| XACML Deny with obligation text | Policy match failed or request malformed | Re-read the deny reason; check `policies/` and the request attributes |
| Validation fails after key or cert change | Key rotation handling | `corridor-ops check-metadata --idp okta` (lists kids); `validate-token` against the JWKS |

Severity rules: anything that breaks a gate blocks flips (severity 1); wrong-mode
acceptance (a token kind accepted outside its mode) is severity 1 security-wise;
cosmetic issues wait for closeout.

## Exit criteria

- All unit suites green with the counts above reproducible from a clean clone.
- Integration suite green including all matrix cells that apply.
- E2E self-bootstrapping suite green from cold start.
- All six gates green with archived runs (Postman/SoapUI/JMeter results).
- The findings ledger ([security-findings-log.md](security-findings-log.md)) shows every
  finding Resolved or explicitly accepted with a reason.
- One rehearsed rollback recorded (a flip back performed in a test environment and its
  audit row verified).
