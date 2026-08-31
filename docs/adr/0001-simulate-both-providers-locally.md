# ADR 0001: simulate both identity providers locally

Status: Accepted

## Context

Corridor demonstrates an ADFS-to-Okta migration for three applications. Real identity
providers mean tenant sign-ups, license terms, rate limits, and somebody's real
directory. A demo depending on a live Okta org and a live ADFS farm stops working the
day the trial lapses and can never be safely shown because the accounts behind it are
real. The protocols themselves (SAML 2.0, OIDC, SCIM 2.0, XACML, SOAP) are open
standards; the value of the demo lives in the protocol behavior, not in any
vendor's console.

## Decision

Implement both providers as local services that speak the real wire protocols:

- `src/Corridor.AdfsSim` (port 8090): a SAML 2.0 IdP with federation metadata at
  `/federationmetadata/2007-06/federationmetadata.xml`, an SSO POST endpoint at
  `/adfs/ls`, and signed assertions (`Saml/SamlResponseBuilder.cs`, `Saml/SamlSigner.cs`).
- `src/Corridor.OktaSim` (port 8080): the target provider, implementing real OIDC
  (discovery, authorize with PKCE, token with refresh rotation and client credentials,
  JWKS with a rotating kid, userinfo, logout: `Endpoints/Oidc.cs`), a SAML IdP mode for
  the portal's dual-trust window (`Endpoints/Saml.cs`), SCIM 2.0 provisioning
  (`Endpoints/Scim.cs`), an XACML PDP (`Xacml/PdpEngine.cs`), and a read-only admin
  persona UI (`Endpoints/Admin.cs`).

Nothing talks to Okta's cloud or any agency system, and all data is synthetic.

## Consequences

- The whole migration, including the cutover, runs on one laptop with
  `scripts/dev-up.sh`; no tenants, no trials, no network beyond localhost.
- The applications under migration consume real protocol artifacts (signed XML, real
  RS256 JWTs, real discovery documents), so the integration risk the demo exists to show
  is genuine and not mocked away.
- The sims are deliberately simplified inside the protocol boundary: a fixed client
  registry with trivial constants, in-memory token stores, a documented subset of XACML.
  Those boundaries are written down in `docs/security.md` rather than left to discover.
- Swap paths to real providers (real Okta authority plus client ids and secrets via
  environment, real ADFS metadata URL plus signing certificate thumbprint, real SCIM base
  URL and token) live in `docs/runbook.md`: a production cutover changes configuration,
  not code.
