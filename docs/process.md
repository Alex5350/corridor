# Corridor process notes: the build narrative

The debugging stories and deliberate choices behind the repo, told the way they happened.
Each story follows the same shape: symptom, investigation, root cause, fix, lesson. The
technical detail lives in the code and the ADRs; this page is about how the knowledge was
earned. Findings IDs refer to [security-findings-log.md](security-findings-log.md).

## Story 1: the stored procedure that worked on my SQL Server

**Symptom.** During live verification of the status transitions, every attempt to close
a traced case came back as a fault. The unit tests for the service layer were green; the
integration tests against the container stack failed on `usp_UpdateStatus` with a CLR
error.

**Investigation.** The transition logic itself was trivial to rule out: calling the proc
with an illegal transition correctly raised state 40001, so the guard was working. The
failing statement was the `Disposition` update, and the error text pointed at CLR
integration. The database was not the SQL Server 2022 instance the procs were drafted
against, it was the `azure-sql-edge` container the compose stack actually runs
(`docker-compose.yml`, chosen for arm64 compatibility).

**Root cause.** `usp_UpdateStatus` used T-SQL `FORMAT()` to stamp the disposition
timestamp. `FORMAT()` shells out to the CLR, and azure-sql-edge ships with CLR disabled,
so the function throws there while behaving perfectly on the developer instance the
script was written against.

**Fix.** Replaced with `CONVERT(NVARCHAR(19), SYSUTCDATETIME(), 120)` in
`db/sql/002_trace_procs.sql` (style 120, the same `yyyy-MM-dd HH:mm:ss` shape). Tracked
as COR-004 in the findings log.

**Lesson.** "Works on my SQL Server" is not done; the verification environment is the
contract. Everything in `db/sql/` now gets exercised against the exact container image
the stack runs, not the instance that happened to be open in the query editor.

## Story 2: the SOAP header that was valid XML and wrong

**Symptom.** The portal's new REST bridge returned a fault on every single call to
TraceLink: `cor:MissingSecurityHeader`. Direct SoapUI calls with the same token worked,
which made the bridge look broken rather than mis-signed.

**Investigation.** Comparing the bridge's envelope with a working SoapUI request showed
the same elements, same token, same SOAP structure. The difference was invisible in a
pretty-printed diff: the `Security` header's namespace. The bridge emitted it in the
service contract namespace (`...tracelink/2026/08`) because that namespace was already
declared and convenient, while the service's inspector searches for the header by exact
namespace `http://corridor.example/security` (the demo's WS-Security-style identity
namespace). A second, subtler mismatch rode along: the JWT variant prefixed the token
element as `cor:jwt`, and the inspector tolerates an optional prefix on that element but
the combination of wrong namespace plus prefix failed both of its lookup paths.

**Root cause.** The client built the header from what looked natural in code rather than
from what the WSDL-observant server actually parses. SOAP header identity is namespace
plus local name, and "it serializes" proves nothing about "it addresses".

**Fix.** `SoapTraceLinkClient` now declares the security namespace explicitly
(`Sec = "http://corridor.example/security"` in
`src/Corridor.Portal/Services/TraceLink/SoapTraceLinkClient.cs`) and emits the `jwt`
element unprefixed, matching `CorridorSecurityMessageInspector.ReadIdentityToken`
exactly. COR-001 in the findings log.

**Lesson.** When two sides agree on XML but disagree on addressing, everything fails
with the least helpful error. The fix that stuck was making the client mirror the
inspector's expectations consciously, and covering both header shapes in tests
(`SecurityHeaderInspectorTests`) so the contract cannot drift silently again.

## Story 3: all-null fields from a well-formed request

**Symptom.** After story 2, `CreateTraceRequest` calls stopped faulting on the header and
started faulting on content: `cor:InvalidRequest`, with the service reporting every
required field null. The JSON arriving at the portal had all values present.

**Investigation.** Logging the raw envelope at the service showed the request element
with all four child elements populated, so the bytes were right and the deserialization
was wrong. The contract type for `TraceRequestCreate` is a DataContract type, and the
elements were being written in the natural, human order: licensee, item, serial,
requester. DataContract members without explicit `Order` settings deserialize in
alphabetical order and, critically, they do not error on out-of-order input: they
silently default to null.

**Root cause.** `DataContractSerializer` wire order is alphabetical unless explicitly
ordered, and mismatched order fails silently rather than loudly. The client was emitting
declaration order; the server was reading DataContract order.

**Fix.** `CreateTraceRequestAsync` now emits the members in DataContract (alphabetical)
wire order, with a comment explaining why the order is load-bearing. The service kept
its null-field rejection as defense in depth, which is what turned a silent integrity
bug into a loud fault in the first place. COR-002 in the findings log.

**Lesson.** Silent failure modes deserve their own tripwires. The validation that
rejected all-null requests was written before the bug appeared, and it is the only
reason this was a five-minute diagnosis instead of a corrupted-row hunt. Also: when a
serializer has an "order" convention, treat the order as part of the contract.

## Story 4: the token decoder that short tokens forgave

**Symptom.** The ops tool's `validate-token` reported signature failures on tokens the
services accepted fine. `decode-token` on the same tokens looked correct at a glance.

**Investigation.** The failures clustered suspiciously: tokens with short payloads
decoded fine, longer ones failed. Diffing the decoder's output byte-for-byte against a
reference decoder showed two characters transposed in the URL-safe alphabet: `-` was
being mapped to `/` and `_` to `+` instead of the other way around.

**Root cause.** The hand-written base64url translation table in
`src/Corridor.Ops.Tool/TokenDecoder.vb` had the two URL-safe substitutions swapped. Any
token segment containing neither `-` nor `_` decoded correctly, and short tokens often
contain neither, so a whole batch of casual tests passed before the alphabet was ever
exercised.

**Fix.** Correct RFC 4648 section 5 mapping (`-` to `+`, `_` to `/`) with padding
restoration, plus decoder tests that deliberately include alphabet-heavy segments
(`tests/Corridor.Ops.Tool.Tests/TokenDecoderTests.vb`). COR-003 in the findings log.

**Lesson.** Test the edge alphabet, not just the happy path: a base64url decoder is only
interesting when the input contains the characters that make it base64url. The 60-test
suite for the tool earned its keep on exactly this class of bug.

## Story 5: port 5000 was never ours

**Symptom.** The portal would not start on a Mac that had done nothing wrong: address in
use, port 5000, nothing visible in `lsof` that looked like a server.

**Investigation.** The occupant was macOS's AirPlay Receiver, which listens on 5000 by
default in recent macOS versions and does not show up where server processes usually do.

**Root cause.** A default OS service squats a port in the classic web-app range, and the
portal's original port choice sat exactly on it.

**Fix.** The portal moved to 5200, consistently: `launchSettings.json`,
`Portal:BaseUrl` in `src/Corridor.Portal/appsettings.json`, the okta-sim client
registry's registered redirect (`http://localhost:5200/signin-oidc` in
`src/Corridor.OktaSim/Models/OAuthClient.cs`), and the integration fixture
(`CorridorStackFixture.PortalPort`). The SPA's portal API default already points at
5200 (`src/Corridor.Spa/src/config.ts`).

**Lesson.** Every Mac developer hits this once. The port is now part of the documented
contract, and `docs/onboarding.md` says so up front rather than letting the next person
rediscover it through a bind error.

## Story 6: the screenshot that looked fine to everyone

**Symptom.** The FieldInsight screenshot in the README rendered as raw HTML: serif text,
blue links, no cards. Reviewers had already looked at it; a fresh capture looked styled,
so it read as a transient capture glitch. It was not.

**Investigation.** Pixel-sampling the committed PNG settled it: the top band was white,
so the navy header had never painted. The page content was rendered (live API data), so
JavaScript had run. In Vite's dev server, CSS arrives as inline style elements injected
by the same module graph, which pointed at the Content-Security-Policy: the dev policy
relaxed `script-src` with `'unsafe-inline'` for the React fast-refresh preamble but not
`style-src`, so the stylesheet was silently blocked on every development render while
preview and production (real CSS files) looked fine. The e2e suite asserted behavior,
never styling, which is why green runs kept shipping an unstyled page.

**Fix.** `'unsafe-inline'` on `style-src` in the development policy only
(`src/Corridor.Spa/vite.config.ts`), the capture script now waits for applied styles
before shooting, and the affected screenshots were retaken.

**Lesson.** Assert rendering with pixels, not opinions: a vision pass had called the
broken shot styled. And a CSP you cannot see failing will fail where nobody looks; dev
differs from prod in exactly the delivery mechanism (inline style elements) the policy
governed.

## Story 7: the signing key that died with the shortest test

**Symptom.** CI-only, intermittent: a token mint returned a 500 whose body was text, not
JSON. Locally nothing reproduced: macOS, a Linux container, the same filter, all green.

**Investigation.** The breakthrough was making it deterministic instead of chasing
probability: throttling a container to 0.6 CPUs reproduced it four runs out of four, and
instrumenting the failing exchange printed the real exception,
`ObjectDisposedException: RSAOpenSsl.TrySignHash`. Every test class boots its own
in-memory host, but all of them load the SAME committed signing PEM, and the token
handler's shared crypto provider cache binds its signature providers to the first loaded
RSA. When the shortest-running class finished and its host disposed that key, every later
token mint across the other parallel hosts died.

**Fix.** Process-lifetime singletons do not dispose their signing keys
(`src/Corridor.OktaSim/Services/SigningKeys.cs`): `Dispose` is a documented no-op, which
is also the correct production lifetime for keys loaded once at startup.

**Lesson.** "Flaky on slower hardware" is a race you have not confined yet; constrain
the CPU and the race stops flirting. And DI disposal is a real trust boundary: disposing
a singleton's resources from one host can break another host's cached handles to the
same key material.

## Deliberate choices worth defending

- **Simulating both providers instead of needing tenants** ([ADR 0001](adr/0001-simulate-both-providers-locally.md)).
  The risk being demonstrated lives in the protocols, and the protocols are open
  standards, so local sims implementing real signed SAML, real OIDC with PKCE, real SCIM
  and XACML deliver the actual learning without trials, licenses, or a real directory.
  The cost is honesty about the simplifications, which `docs/security.md` pays in full.
- **CoreWCF on .NET 10** ([ADR 0004](adr/0004-token-header-for-soap.md)). The tempting
  shortcut is stubbing the SOAP service or rewriting it as REST. Keeping the real
  ASMX-style wire format (basicHttpBinding, SOAP 1.1, `?wsdl`) on a modern host is what
  makes the identity swap realistic; the dispatch inspector extension point is exactly
  where a dual-token profile belongs.
- **The VB.NET ops tool** ([ADR 0009](adr/0009-vb-ops-tool.md)). A small operator
  console in the language this tool class grew up in, in the same solution and CI as the
  C# services. It demonstrated the point that matters in these estates: the toolchain
  carries the whole mix, and the boring utility is allowed to stay boring.
- **Raw ADO.NET left alone** ([ADR 0005](adr/0005-keep-raw-adonet.md)). Not laziness:
  an identity migration that also rewrites the data layer cannot attribute its
  regressions. The discipline paid off concretely in story 1, where a database-level
  issue surfaced precisely because nothing else was in the blast radius.
- **Tests that earn the war stories.** Every story above ended by asking which test
  would have caught it, and that test now exists: header-shape tests (story 2),
  null-field rejection (story 3), alphabet-heavy decoder tests (story 4),
  live-container verification of SQL scripts (story 1).
