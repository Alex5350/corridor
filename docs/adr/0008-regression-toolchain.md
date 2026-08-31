# ADR 0008: a committed API regression toolchain between every phase

Status: Accepted

## Context

The cutover has a dangerous property: the system under test is multi-protocol (OIDC,
SAML, SCIM, XACML, SOAP) and the thing being changed (which tokens are trusted) alters
behavior at the protocol edges, not in obvious UI spots. Unit tests cover components;
integration tests cover the .NET stack's own paths. Neither answers the operator's actual
question at 11pm mid-flip: "does the estate still answer the same as it did an hour ago,
from a tool that is not our code?" Commercial API tools also let suites rot on someone's
laptop, and doubling the in-repo suite would still be this codebase grading itself.

## Decision

Commit three regression artifacts and run them between every migration phase:

- `postman/Corridor.postman_collection.json`: environment-variable driven; folders for
  Health, the full OIDC code + PKCE dance scripted in Postman test scripts, SCIM CRUD,
  XACML decide, portal REST, and a SoapUI-parity SOAP call.
- `soapui/Corridor-TraceLink-soapui-project.xml`: a real SoapUI 5.x project against the
  live WSDL at `http://localhost:8000/TraceLink.svc?wsdl`, one test request per TraceLink
  operation, SAML and JWT header variants, and a TestSuite with SOAP Fault, schema, and
  XQuery assertions on CaseNumber.
- `jmeter/corridor-flow.jmx`: a "Portal read path" thread group (login -> permits ->
  cases loop) and a "SCIM write path" group, over a CSV of synthetic users, with
  response-code and JSON assertions.

CI parse-checks the artifacts' well-formedness (the `artifacts` job in
`.github/workflows/ci.yml`), and `docs/test-plan.md` defines the per-phase gate: same
contracts, run in each TrustMode, compared green before the next flip.

## Consequences

- Every phase gets an independent verdict from outside the codebase under test, which is
  the only kind of verdict that builds flip-window confidence.
- The suite is vendor-tool readable: a tester who knows Postman or SoapUI can extend it
  without learning this repo's test framework, which matches how agency QA teams staff a
  cutover weekend.
- Maintenance is a real cost: three artifacts must track any contract change, and CI's
  parse check catches malformed files, not stale assertions; the per-phase gate catches
  staleness in practice.
- JMeter results are qualitative here (does the path hold, do assertions pass under
  repetition); the repo claims no load numbers, per `docs/test-plan.md`.
