# Postman collection

`Corridor.postman_collection.json` with the example environment `corridor-env.json` is the
between-phases API regression gate: the same requests run before and after every trust
mode flip, so a regression in any provider, the bridge, or the SOAP surface shows up as a
diff in outcomes rather than a surprise in a browser.

## Running

    npx newman run Corridor.postman_collection.json -e corridor-env.json

Prerequisites: the full stack up (`scripts/dev-up.sh`), seeded database, and the legacy
service in Dual or Okta trust mode for the SOAP folder (the seeded default is Adfs, which
correctly rejects JWTs with cor:InvalidIdentityMode):

    docker exec -it $(docker ps -qf name=db) /opt/mssql-tools18/bin/sqlcmd -C -S localhost \
      -U sa -P CorridorDev1! -d Corridor \
      -Q "UPDATE idn.MigrationApps SET TrustMode='Dual' WHERE AppKey='legacy'"

Flip it back with the same command when done. The collection description carries the same
note so the requirement travels with the file.

## What each folder proves

- Health: all four services answer.
- OIDC: the full PKCE dance scripted in collection JavaScript (crypto.subtle for the S256
  challenge), token shape and JWT payload assertions, and the client-credentials service
  token that the SOAP folder reuses as {{jwt}}.
- SCIM: CRUD against /scim/v2/Users with scim+json content-type assertions.
- XACML: raw decision requests for all three committed policies plus a malformed one.
- Portal REST: JSON over the bridge, including the RFC 9457 problem shape for an unknown
  case (404 with the cor:CaseNotFound subcode).
- SOAP: hand-built envelopes against TraceLink with the cor:Security header carrying the
  JWT, plus a deliberate bad-token request asserting the fault.
