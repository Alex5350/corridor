# Corridor: architecture and integration contract

Corridor is an ADFS-to-Okta identity migration program for three synthetic federal
applications. Every IdP is a LOCAL simulation implementing the REAL protocol (actual signed
SAML 2.0 XML, actual OIDC code + PKCE with JWKS, actual SCIM 2.0, actual XACML decisions,
actual SOAP/WSDL). Nothing talks to Okta's cloud or any real agency system; swap paths to
the real services are documented in docs/runbook.md. All data is synthetic.

## Services, ports, and layout

| Service | Path | Port | Tech |
|---|---|---|---|
| okta-sim ("Okta" target IdP) | src/Corridor.OktaSim | 8080 | ASP.NET Core minimal APIs |
| adfs-sim ("ADFS" legacy IdP) | src/Corridor.AdfsSim | 8090 | ASP.NET Core MVC (SAML POST profiles) |
| portal (PermitPortal app) | src/Corridor.Portal | 5000 | ASP.NET Core MVC, OIDC auth-code |
| legacy (TraceLink SOAP service) | src/Corridor.Legacy | 8000 | ASP.NET Core + CoreWCF, ADO.NET |
| spa (FieldInsight inspector app) | src/Corridor.Spa | 5173 | React 19 + Vite, OIDC PKCE public client |
| ops tool (VB.NET) | src/Corridor.Ops.Tool | CLI | VB.NET console (metadata/token validation) |
| integration tests | tests/Corridor.IntegrationTests | xUnit + Testcontainers |
| e2e | e2e/ | Playwright, self-bootstrapping |

Shared solution: Corridor.sln at repo root. Target net10.0 everywhere; LangVersion latest;
Nullable enable; TreatWarningsAsErrors true (Directory.Build.props, already committed).

## Identity flows (the point of the demo)

1. PRE-MIGRATION: all three apps trust adfs-sim only.
   - adfs-sim is a SAML 2.0 IdP: metadata at
     http://localhost:8090/federationmetadata/2007-06/federationmetadata.xml,
     SSO POST endpoint /adfs/ls. It issues signed SAML 2.0 assertions (signed XML,
     dev certificate in src/Corridor.AdfsSim/DevCerts/, password corridor-dev-only,
     committed deliberately: synthetic, no secret value).
2. CUTOVER (dual trust): per-app flag in the idn.MigrationAppStatus table flips each app
   to accept BOTH providers; the portal demonstrates a live toggle.
3. POST-MIGRATION: apps trust okta-sim only:
   - portal: OIDC authorization-code flow (confidential client).
   - spa: OIDC authorization-code + PKCE S256 (public client).
   - legacy SOAP service: JWT bearer issued by okta-sim, carried in a WS-Security-style
     header (see below).

## okta-sim endpoints (implement ALL)

OIDC:
- GET /.well-known/openid-configuration (issuer http://localhost:8080)
- GET /authorize (response_type=code, PKCE S256 REQUIRED for public clients)
- POST /token (grant_type authorization_code + refresh_token; client auth via
  Authorization: Basic for confidential clients)
- GET|POST /jwks (RS256 keys; rotate by including two kids)
- GET /userinfo (Bearer)
- GET /logout (post_logout_redirect_uri)
Registered clients (in-memory seed from db): portal (confidential, secret
corridor-portal-secret, redirect http://localhost:5200/signin-oidc), spa (public, redirect
http://localhost:5173/callback, PKCE required), legacy (confidential, secret
corridor-legacy-secret, no redirect: client-credentials for service tokens).
SAML IdP mode (for portal dual-trust): GET /saml/metadata, POST /saml/sso issuing signed
assertions to the portal's ACS http://localhost:5200/saml/acs.
SCIM 2.0: /scim/v2/Users GET (list, filter by userName eq), POST, GET {id}, PUT, PATCH
(only replace-op on active and groups). Bearer token: corridor-scim-token.
XACML PDP: POST /pdp/decide: request body is an XACML 2.0/3.0 <Request> XML document;
response is a REAL XACML <Response><Result><Decision>Permit|Deny</Decision> XML, decided
against policies/policies/*.xacml.xml (role + resource + action attributes). Errors return
a Deny with Obligation text, never 500 without XML.
Admin persona UI at / (a simple "Okta-style" admin console page: user list from SCIM
store, app list, per-app IdP assignment): READ-ONLY, server-rendered, no JS framework.

## adfs-sim endpoints

- GET /federationmetadata/2007-06/federationmetadata.xml (EntityDescriptor with
  IDPSSODescriptor, X509 cert, SSO POST binding to /adfs/ls)
- GET / (forms-style login page, synthetic users seeded from db: see db/seed)
- POST /adfs/ls (username/password check against seed, then POST back to the app ACS a
  signed SAMLResponse: assertions carry NameID + claims: upn, role)
Token lifetime 60 minutes, NotBefore skew 5 minutes (realistic clock skew handling).

## legacy (TraceLink) SOAP service contract (WSDL-first behavior via CoreWCF)

Service: TraceLinkService, basicHttpBinding, SOAP 1.1, endpoint /TraceLink.svc,
mex enabled at /TraceLink.svc?wsdl. Namespace http://corridor.example/tracelink/2026/08.
Operations:
- TraceCase[] SearchCases(string requester, string statusFilter, int maxRows)
- TraceCase GetCase(string caseNumber)
- string CreateTraceRequest(TraceRequestCreate request)  // returns new caseNumber TRC-######
- bool UpdateStatus(string caseNumber, string newStatus, string actor)
TraceCase fields: CaseNumber, LicenseeName, ItemDescription, Serial, Status,
  SubmittedAt, SubmittedBy, Disposition.
TraceRequestCreate fields: LicenseeName, ItemDescription, Serial, RequesterUpn.
Security: every call carries SOAP header <cor:Security> containing EITHER
  <saml:Assertion> (ADFS mode; validated: signature, audience, NotOnOrAfter) OR
  <jwt> (Okta mode; validated: signature via okta-sim JWKS, iss/aud/exp). Mode per-call;
  both honored while the app's MigrationAppStatus.TrustMode = Dual, only JWT when Okta,
  only SAML when Adfs. Wrong-mode token -> SOAP Fault with subcode
  cor:InvalidIdentityMode.
Data access: raw ADO.NET ONLY (SqlConnection, SqlCommand, SqlParameter, SqlDataReader)
  against stored procedures in db/sql/trace/; no ORM in this service (that is the point).
Statuses: Received, UnderReview, Traced, Closed, Rejected.

## portal (PermitPortal)

MVC app, OIDC confidential client against okta-sim; SAML-SP fallback against BOTH IdPs for
the dual-trust demo (middleware: if MigrationAppStatus.TrustMode for portal says Dual,
accept either). Pages: Home (federal-plain styling, USWDS-lite: system font stack, plain
blue header, no external CDN), Permits (list/apply for import permit applications:
licensee, item, quantity, purpose), Cases (REST proxy: /api/cases -> calls the legacy SOAP
service, translating JSON<->SOAP: the REST-to-SOAP migration bridge), and Admin >
Migration dashboard: table of the three apps with TrustMode (Adfs/Dual/Okta), live
flip-button (writes idn.MigrationAppStatus), last cutover event, audit trail list.
REST API under /api/cases mirrors SearchCases/GetCase/CreateTraceRequest with JSON +
problem-details errors (RFC 9457). Auth: [Authorize] on app pages; /api/cases accepts the
caller's portal token (delegation to SOAP uses the portal's own legacy-client JWT from
okta-sim client-credentials when in Okta/Dual mode, or a service SAML assertion in Adfs
mode).

## spa (FieldInsight)

React 19 + Vite + TypeScript. oidc-client-ts against okta-sim (public client, PKCE).
Views: assignments list (from /api/assignments on the portal: seeded inspection
assignments), assignment detail with checklist toggle (local state + PATCH to portal
/api/assignments/{id}), profile card showing raw ID token claims (decoded, visible for the
demo). No UI framework dependency beyond react; plain CSS file; accessible (labels,
focus states); vitest unit tests for the checklist reducer and token-claim display.

## Database (SQL Server; T-SQL)

Connection: Server=localhost,1433;Database=Corridor;User Id=sa;Password=CorridorDev1!
(compose service db; azure-sql-edge image for arm64 compatibility: reference
mediflow's docker-compose.yml at /Users/alex/portfolio/mediflow/docker-compose.yml for the
working local pattern).
Schemas and objects (create in db/sql/ as ordered, idempotent scripts; seed in
db/sql/seed/):
- perm schema: ImportPermits(Id, PermitNumber, LicenseeName, ItemDescription, Quantity,
  Purpose, Status, SubmittedAt, SubmittedBy)
- trace schema: TraceCases(CaseNumber PK, LicenseeName, ItemDescription, Serial, Status,
  SubmittedAt, SubmittedBy, Disposition) + procs: usp_SearchCases, usp_GetCase,
  usp_CreateTraceRequest, usp_UpdateStatus (status transition validation inside proc:
  only legal transitions, else RAISERROR with state 40001)
- idn schema: Users(Id, Upn, DisplayName, Role, PasswordHash(sha256 of
  'corridor-demo-'+password, documented as demo-only), ScimExternalId, Active),
  MigrationApps(AppKey PK, AppName, TrustMode, LastFlippedAt, FlippedBy),
  AuditEvents(Id, At, Actor, AppKey, Event, Detail)
Seed: 4 users (admin@corridor.example role Admin; inspector@... Inspector;
officer@... Officer; clerk@... Clerk, password Demo1234! for all, documented on the login
pages), 3 MigrationApps rows (portal/legacy/spa all starting TrustMode=Adfs), 8 permit
rows, 12 trace cases, 6 assignments for the spa (idn.Assignments: Id, InspectorUpn,
LicenseeName, Focus, DueAt, ChecklistJson).
Legal trace transitions: Received->UnderReview->Traced->Closed; Received->Rejected;
UnderReview->Rejected. Closed/Rejected terminal.

## Test artifacts (the job's tooling, committed as REAL files)

- postman/Corridor.postman_collection.json: environment-variable driven; folders: Health,
  OIDC (full code+PKCE dance scripted in Postman test scripts), SCIM CRUD, XACML decide,
  Portal REST, SoapUI-parity SOAP call via Postman.
- soapui/Corridor-TraceLink-soapui-project.xml: real SoapUI 5.x project XML: interface
  imported from the live WSDL shape above, 4 test requests (one per operation) with SAML
  and JWT header variants, a TestSuite with assertions (SOAP Fault, schema, XQuery on
  CaseNumber).
- jmeter/corridor-flow.jmx: ThreadGroup "Portal read path" (login -> /permits ->
  /api/cases loop), one "SCIM write path", CSV of synthetic users; assertions on response
  code and a JSON assertion; documented how to run: jmeter -n -t ... -l results.jtl.

## deploy/ artifacts (documented, not cloud-deployed)

- deploy/rhel/Dockerfile.ubi9 (per-service base pattern: registry.access.redhat.com/ubi9,
  dotnet runtime install, non-root user), systemd unit examples (corridor-portal.service
  etc. with Environment= overrides), README on RHEL assumptions (firewalld ports,
  SELinux boolean for the SQL connection).
- deploy/aws/corridor-ecs.yaml: CloudFormation skeleton (VPC-lite placeholders, ECS
  service + task definitions for the three apps and okta-sim, Secrets Manager refs for
  client secrets: names only, no values).
- .gitlab-ci.yml (mirror of the GitHub workflow stages: build/test/integration) and
  Jenkinsfile (declarative pipeline with the same stages), both small, honest, commented
  "kept in parity with ci.yml; this repo runs on GitHub Actions".
- sonar-project.properties + docs/security-findings-log.md (a findings ledger: seeded with
  5 plausible resolved findings: e.g. "XML parser was XmlDocument with ProhibitDtd unset ->
  switched to safe settings" etc., each with fix commit reference placeholders replaced by
  real commits at the end).

## Conventions (portfolio-wide, non-negotiable)

- NO em-dashes or en-dashes in ANY file (code comments included). Use commas,
  colons, semicolons, parentheses, or " - ".
- No real secrets, no real agency data, synthetic everything; disclaimers in README.
- No AI-assistant instruction files (no AGENTS.md/CLAUDE.md) anywhere.
- Error discipline: RFC 9457 problem details on REST; SOAP Faults with subcodes on SOAP;
  XACML Deny-with-reason on PDP errors.
- Every service: /healthz endpoint (anonymous, JSON {status:"ok"}).
- Tests: xUnit per service (unit), tests/Corridor.IntegrationTests (Testcontainers SQL +
  real HTTP against boot services), e2e/ Playwright self-bootstrapping (creates db if
  missing, boots compose, drives the three login flows and the cutover toggle).
- Logging: Serilog console; OpenTelemetry traceparent propagation headers honored in the
  portal->legacy hop (correlation id shown in audit events).
