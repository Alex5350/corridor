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

Centralize authorization decisions in a policy decision point on the target provider, and
make the portal a real policy enforcement point (PEP) at its API boundary.

The PDP:

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

The portal PEP:

- `src/Corridor.Portal/Auth/Pdp/` owns `IPdpClient` and `PdpHttpClient`: a named
  HttpClient (`pdp`, 3 second timeout, one retry on a transient failure, base URL from
  `Portal:PdpBaseUrl`) that builds the XACML 2.0 request context with XmlWriter, so the
  role claim always lands in an attribute VALUE and never in concatenated markup. Real
  decisions are cached 15 minutes per (role, resource, action) triple on the same
  TimeProvider pattern the legacy JWKS provider uses; synthetic fail-closed denials are
  not cached, so a recovered PDP is consulted on the very next call.
- Fail closed is non-negotiable: an unreachable PDP, an HTTP error, or a response whose
  Decision cannot be parsed becomes a Deny with exactly one warning logged, and nothing
  throws past the enforcement point.
- Guarded endpoints: GET list, GET by id, and POST create on `/api/cases` (resource
  `trace-cases`), and PATCH `/api/assignments/{id}` (resource `assignments`, action
  `write`) as defense in depth after the endpoint's own ownership check. Policy 10 has
  no create rule, so the create verb is authorized under the trace-cases read permit
  until `policies/` grows a `trace-cases:create` file, which the PEP then honors without
  a portal release. A Deny surfaces as a 403 problem detail with errorCode
  `cor:PdpDenied` and the PDP status message in the detail.
- Authentication stays local: the AnyRole and SpaBearer policies remain the gate that
  proves who the caller is, and the PDP then decides what that caller may do. A Clerk
  token authenticates fine and is still denied trace case reads, which is the point.
- Razor pages (Permits, Cases, Admin) stay on plain role attributes. The API boundary is
  the enforcement point because it is the machine contract shared by the bridge and SPA
  callers and the surface an auditor probes; per-page PDP calls would multiply decision
  traffic for a single server-rendered principal without adding a trust boundary.

## Consequences

- Who-can-do-what is one reviewable artifact instead of three codebases; changing a rule
  during the cutover window is a file change, not a release. Default-deny holds by
  construction: anything the permit policies do not match falls to the deny-all, and the
  portal encodes every PDP failure as a Deny.
- Clerk reads of `/api/cases` legitimately return 403 now; before the PEP, the AnyRole
  gate let any of the four roles read the bridge.
- The 15 minute decision cache trades instant revocation for decision throughput; the
  access tokens themselves live 15 minutes, so the exposure window is the same order.
- Because policy 20 permits only Inspectors, the live PDP also denies Admin assignment
  writes at the portal check; the ownership rule stays the first gate and the PDP the
  second, and widening either is a policy file change.
- The subset keeps the demo honest without pretending to a full XACML 3.0
  implementation (no obligations on Permit, no custom functions); the boundary is
  written into the code docs and `docs/GLOSSARY.md`.
- Covered by `XacmlTests` and `PdpEnforcementTests` (unit, against a scripted fake PDP),
  and by `XacmlDecisionTests` and `PortalPepEnforcementTests` (integration, against the
  live PDP booted by the stack fixture).
