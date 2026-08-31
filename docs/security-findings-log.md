# Corridor security and quality findings log

The findings ledger for static and web scanning of this repo, in the style of a
SonarQube/Tenable register but tool-agnostic: ID, severity, description, the fix as it
landed in the code, status. Scanning configuration is `sonar-project.properties` (the
scanner runs outside CI by choice; see `docs/security.md`). Descriptions are written so
they hold regardless of which scanner reported them.

| ID | Severity | Component | Finding | Fix | Status |
|---|---|---|---|---|---|
| COR-001 | High | portal bridge | The SOAP client emitted its `cor:Security` header in the service contract namespace (`http://corridor.example/tracelink/2026/08`) instead of the identity namespace, and prefixed the `jwt` element (`cor:jwt`), so the dispatch inspector could not find a header it would accept; every bridge call returned `cor:MissingSecurityHeader`, masking the real validation path during integration | Header built with the dedicated security namespace `http://corridor.example/security` and an unprefixed `jwt` element, matching what `CorridorSecurityMessageInspector` parses; assertions written to answer both shapes; regression covered in `tests/Corridor.Legacy.Tests/SecurityHeaderInspectorTests.cs` and the SoapUI SAML/JWT variants | Resolved |
| COR-002 | High | portal bridge | `CreateTraceRequest` sent DataContract members in declaration order, but `DataContractSerializer` expects wire (alphabetical) order and silently deserialized out-of-order members as null; the live symptom was `cor:InvalidRequest` with all-null fields, an input-validation bypass risk and a silent data-integrity failure | Members emitted in DataContract order in `SoapTraceLinkClient.CreateTraceRequestAsync` (ItemDescription, LicenseeName, RequesterUpn, Serial), with a comment tying the order to the serializer; null-field rejection in `TraceLinkService.CreateTraceRequest` kept as defense in depth | Resolved |
| COR-003 | Medium | ops tool | The base64url decoder transposed the URL-safe alphabet mappings (`-` mapped to `/` and `_` to `+`); tokens short enough to contain neither character decoded correctly, masking the bug until RS256 signature verification failed on longer tokens | Correct RFC 4648 section 5 mapping (`-` to `+`, `_` to `/`) with padding restoration in `TokenDecoder.Base64UrlDecode` (`src/Corridor.Ops.Tool/TokenDecoder.vb`); decoder table tests added including alphabet-heavy tokens (`tests/Corridor.Ops.Tool.Tests/TokenDecoderTests.vb`) | Resolved |
| COR-004 | Medium | database | `trace.usp_UpdateStatus` used the T-SQL `FORMAT()` function, which requires CLR integration and fails on azure-sql-edge (CLR disabled); the procedure errored at flip-verification time on the container stack the demo actually runs on | Replaced with `CONVERT(NVARCHAR(19), SYSUTCDATETIME(), 120)` style 120 formatting in `db/sql/002_trace_procs.sql`; verified live against the azure-sql-edge container during the status-transition checks | Resolved |
| COR-005 | Medium | okta-sim PDP | XACML request parsing originally used permissive `XmlDocument` loading without explicit DTD and resolver settings; a crafted request with a DTD or external entity could have consumed memory or fetched external resources from the PDP process | All XACML parse paths now go through `SafeXml.ReaderSettings` (`DtdProcessing.Prohibit`, `XmlResolver = null`, `CheckCharacters`) in `src/Corridor.OktaSim/Saml/SafeXml.cs`, used by `PdpEngine.ParseRequest` and `XacmlPolicyParser.Parse`; malformed input returns a real XACML Deny with a StatusMessage | Resolved |
| COR-006 | Medium | okta-sim tokens | The refresh-token store initially allowed an already-consumed refresh token to be presented again without consequence, leaving a replay window on long-lived credentials | Single-use enforcement with family revocation in `RefreshTokenStore.Redeem` (`src/Corridor.OktaSim/Services/RefreshTokenStore.cs`): replay marks every sibling in the family revoked, and the token endpoint returns `invalid_grant` with a logged reuse warning; covered by the refresh-flow unit and integration tests | Resolved |

## Notes

- Every finding above is Resolved; none carry an accepted-risk exception. The demo-grade
  choices (hashing, secrets, http, no rate limits) are deliberate and documented in
  `docs/security.md`; they are scope, not findings, because fixing them would change
  what the demo teaches rather than harden it.
- Fix verification: each row names the test or live check that would catch a regression
  (the SoapUI variants and `TokenValidatorModeGatingTests` for COR-001; the ops tool's
  60 unit tests for COR-003; the status-transition suite for COR-004).
