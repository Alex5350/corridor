# ADR 0007: a central XACML policy decision point

Status: Accepted

## Context

Before the migration, authorization rules live scattered in the applications: role checks
buried in page controllers, and worse, implied by which IdP claims happened to arrive.
During a dual-trust cutover the claim shapes differ between providers (a SAML assertion
from adfs-sim and an ID token from okta-sim both carry `upn` and `role`, but nothing
guarantees that forever). If each app keeps deciding authorization on its own, every
claim-shape change becomes a per-app code change at the worst possible time. A homegrown
"roles.json" would centralize the file but not the semantics; XACML gives the decision
both a standard request shape and a standard answer, which is what an auditor wants to
see.

## Decision

Centralize authorization decisions in a policy decision point on the target provider:

- okta-sim exposes `POST /pdp/decide`, which takes a real XACML 2.0/3.0 request context
  and returns a real `<Response><Result><Decision>` document
  (`src/Corridor.OktaSim/Endpoints/Xacml.cs`, `Xacml/PdpEngine.cs`).
- Policies are reviewable files in `policies/`, loaded at startup in filename order so
  the deny-all sorts last: `10-trace-read-officers-admins.xacml.xml` (Officers and
  Admins may read trace cases), `20-assignments-write-inspectors.xacml.xml` (Inspectors
  may write assignments), `90-deny-all.xacml.xml`. Decisions are role + resource +
  action triples with first-applicable combining and a default deny.
- Errors return a Deny with a StatusMessage and an Obligation, never a naked 500; a
  request with no applicable policy is also a Deny (the safe encoding of NotApplicable).
- The engine is a documented, deliberate subset of XACML (single-valued string
  attributes, string-equal matching), with an in-code fallback copy of the same three
  policies so tests and stripped deployments still have a working PDP.

## Consequences

- Who-can-do-what is one reviewable artifact instead of three codebases; changing a rule
  during the cutover window is a file change, not a release. Default-deny holds by
  construction: anything the permit policies do not match falls to the deny-all.
- The subset keeps the demo honest without pretending to a full XACML 3.0
  implementation (no obligations on Permit, no custom functions); the boundary is
  written into the code docs and `docs/GLOSSARY.md`.
- Applications still enforce authentication locally; the PDP centralizes the
  authorization decision, so the trust boundary fails closed (Deny) when the PDP is
  unreachable. Covered by `XacmlTests` (unit) and `XacmlDecisionTests` (integration).
