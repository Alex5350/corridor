# ADR 0009: a small VB.NET console tool for the operator

Status: Accepted

## Context

During the cutover window, the operator's questions are narrow and urgent: is the
federation metadata sane, what is actually inside this token, is the signature good, did
the SCIM bridge carry the account over. Answering them with curl plus hand-piped base64
plus a JWT-decoding website is slow and leaks tokens into browser tabs. A full internal
admin web app is overkill and becomes its own thing to trust during a security-sensitive
window. A C# console tool was the default choice; VB.NET was chosen instead, on purpose.

## Decision

Build `corridor-ops` (`src/Corridor.Ops.Tool`) as a small, dependency-light console
tool, and write it in VB.NET:

- Commands: `check-metadata` (ADFS federation metadata or OIDC discovery, with JWKS kid
  listing), `decode-token` (no validation, local-time expiry warnings), `validate-token`
  (RS256 against a JWKS url or file, plus issuer, audience, expiry, not-before, each
  check reported as passed, failed, or skipped), `whoami-token` (upn + role summary),
  and `scim-dump` (SCIM user table). Full usage with exit codes lives in
  `src/Corridor.Ops.Tool/USAGE.md`.
- Deterministic exit codes (0 ok, 1 usage, 2 invalid metadata, 3 unreachable, 4 invalid
  token, 5 SCIM error) so flip-window scripts can branch on results.
- Every network call has a five second timeout; the SCIM bearer token is sent but never
  printed; `NO_COLOR` switches off ANSI output for log capture.

VB.NET is a deliberate choice, not an accident: operator consoles in this domain grew up
on VB, the tool is exactly the kind of small, boring utility VB has always been good at,
and it demonstrates that the .NET toolchain carries the whole estate (C# services, VB
operator tool, one `Corridor.slnx`, one CI).

## Consequences

- The operator carries one command that answers the flip-window questions in seconds,
  offline of any web UI; the runbook's token-troubleshooting table is built entirely on
  it (`docs/runbook.md`).
- The tool shares the solution, CI, and unit-test conventions (60 tests in
  `tests/Corridor.Ops.Tool.Tests/`), so it cannot silently rot; that suite earned its
  keep on the base64url alphabet bug (`docs/process.md`, finding COR-003).
- Scope stays deliberately small: the tool validates and inspects, it never mutates
  migration state; flips belong to the portal dashboard or the documented SQL procedure,
  so the tool needs no write privileges on `idn.MigrationApps` at all.
