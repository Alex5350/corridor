# corridor-ops usage

corridor-ops is the little console tool an operator carries during the
Corridor identity cutover weekend. It checks IdP federation metadata, decodes
and validates JWTs, and lists SCIM users, all against the local simulations
(okta-sim on http://localhost:8080, adfs-sim on http://localhost:8090). Every
outgoing call has a 5 second timeout. Set the NO_COLOR environment variable to
any non-empty value to switch off ANSI colors.

Build and run:

    dotnet build src/Corridor.Ops.Tool
    dotnet run --project src/Corridor.Ops.Tool -- <command> [options]

## Commands

### check-metadata

Fetches federation metadata and sanity checks it before a cutover step.

    corridor-ops check-metadata --idp adfs|okta [--url URL]

Defaults:

- adfs: http://localhost:8090/federationmetadata/2007-06/federationmetadata.xml
- okta: http://localhost:8080/.well-known/openid-configuration

For adfs it parses the SAML 2.0 EntityDescriptor with DTD prohibited, then
prints the entity id, the SSO endpoint with its binding, and the signing
certificate thumbprint, subject, and expiry. For okta it parses the OIDC
discovery document, prints the issuer and endpoints, and also lists the JWKS
key ids (a JWKS fetch failure is a warning, not a failed check).

    corridor-ops check-metadata --idp adfs
    corridor-ops check-metadata --idp okta --url http://localhost:8080/.well-known/openid-configuration

Exit codes: 0 ok, 2 invalid metadata, 3 unreachable.

### decode-token

Decodes a JWT and prints the header and payload claims as a table, without
validating anything. Numeric exp/nbf/iat/auth_time claims also show their
local time, and stale tokens raise EXPIRES or NOT-YET-VALID warnings.

    corridor-ops decode-token <jwt>

Exit codes: 0 ok, 1 usage, 4 malformed token.

### validate-token

Validates an RS256 JWT signature against a JWKS, plus issuer, audience,
expiry, and not-before. The JWKS comes from an http(s) url or a local file.
Expected issuer and audience are optional; a check without an expectation is
reported as skipped and does not fail the run.

    corridor-ops validate-token <jwt> --jwks <url|path> [--iss ISSUER] [--aud AUDIENCE]

Examples:

    corridor-ops validate-token <jwt> --jwks http://localhost:8080/jwks --iss http://localhost:8080 --aud legacy
    corridor-ops validate-token <jwt> --jwks ./certs/okta-jwks.json

Checks: structure, algorithm, key lookup, RS256 signature, issuer, audience,
expiry, not-before. Exit codes: 0 all checks pass, 4 any check fails.

### scim-dump

GETs the SCIM 2.0 user list and prints a table of userName, active, and
externalId. Long values are truncated so the table stays readable. The bearer
token is sent in the Authorization header and never printed.

    corridor-ops scim-dump --url URL --token TOKEN

The --url value may be the service base url (the /scim/v2/Users path is
appended) or the full endpoint url.

    corridor-ops scim-dump --url http://localhost:8080 --token corridor-scim-token

Exit codes: 0 ok, 5 scim endpoint error.

### whoami-token

Convenience wrapper around decode-token: prints an XML summary of the upn
claim (falling back to preferred_username, then sub) and the role claim
(falling back to groups), plus expiry warnings.

    corridor-ops whoami-token <jwt>

Exit codes: 0 ok, 1 usage, 4 malformed token.

## Exit code table

| Code | Name            | Meaning                                              |
|------|-----------------|------------------------------------------------------|
| 0    | Success         | command completed successfully                       |
| 1    | Usage           | unknown command or missing/invalid arguments         |
| 2    | InvalidMetadata | fetched metadata is malformed or the wrong document  |
| 3    | Unreachable     | endpoint could not be reached (5 s timeout)          |
| 4    | InvalidToken    | token failed validation or is malformed              |
| 5    | ScimError       | SCIM endpoint returned an error or bad payload       |
