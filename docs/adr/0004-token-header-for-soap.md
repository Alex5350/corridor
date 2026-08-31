# ADR 0004: swap the identity inside the SOAP header, keep the SOAP service

Status: Accepted

## Context

TraceLink (`src/Corridor.Legacy`) is the classic agency asset: a SOAP 1.1 service,
WSDL-first, consumed by systems that cannot be rewritten on the migration's schedule.
Replacing it with a REST API would break every caller at once, which is exactly the
downtime the program promises to avoid. The thing that must change is who vouches for
the caller, not the wire contract. Full WS-Security/WS-Trust was set aside: it adds
WS-* plumbing without clarifying the identity migration story.

## Decision

Keep the contract untouched (namespace, four operations, BasicHttpBinding via CoreWCF)
and accept a WS-Security-style SOAP header carrying either token:

- A dispatch inspector (`CorridorSecurityMessageInspector`) reads
  `<cor:Security xmlns:cor="http://corridor.example/security">` and extracts either a
  `saml:Assertion` or a `jwt` element.
- A `TokenValidator` facade gates the token kind against the app's TrustMode, then
  delegates to `SamlTokenValidator` (signed XML, certificate-pinned, audience, lifetime
  with five minute skew) or `JwtTokenValidator` (RS256 via the okta-sim JWKS, issuer,
  audience, one minute skew).
- Rejections surface as SOAP faults with `cor:` subcodes (`CorridorFault.cs`),
  including `cor:InvalidIdentityMode` for a right-token-wrong-mode call.
- The portal's REST-to-SOAP bridge speaks this header both ways
  (`SoapTraceLinkClient`), minting a service SAML assertion in Adfs mode or a
  client-credentials JWT in Dual/Okta mode (`LegacyCredentialFactory`).

The production alternative (real WS-Trust) is noted in the inspector's doc comment.

## Consequences

- SOAP callers keep working through the whole migration; callers that already send
  SAML (the pre-migration state) need no change at all.
- Dual trust on the SOAP surface is literally "accept either element in one header",
  easy to verify by hand in SoapUI (the committed project carries SAML and JWT
  variants) and per mode in tests (`SecurityHeaderInspectorTests`).
- The simplification was paid for twice in practice: the header namespace and `jwt`
  element shape, and the DataContract member order, were real debugging stories
  (`docs/process.md`, findings COR-001 and COR-002).
- Full WS-Trust stays open later because validation is isolated behind
  `ITokenValidationStrategy`; a new token kind slots in without touching the contract.
