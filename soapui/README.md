# SoapUI project

`Corridor-TraceLink-soapui-project.xml` is a SoapUI 5.x project against the live WSDL at
http://localhost:8000/TraceLink.svc?wsdl (namespace
http://corridor.example/tracelink/2026/08).

## Running

Open the project in SoapUI and, with the stack up (`scripts/dev-up.sh`):

1. The Regression suite: four requests (one per operation) with schema, XPath, and
   not-fault assertions. UpdateStatus consumes CreateTraceRequest's returned case number
   through property expansion, so the suite re-runs cleanly.
2. The Identity modes suite: a Groovy step first mints a service JWT from okta-sim
   (client credentials), then the same SearchCases runs twice: once with that JWT in the
   cor:Security header (expects success) and once with a deliberately bad token (expects
   the cor:InvalidToken fault).

The trust mode note from ../postman/README.md applies here too: with legacy in Adfs mode
the JWT call correctly fails with cor:InvalidIdentityMode, which is itself an assertion
worth watching once.

## Why a committed project file

The header shape (cor:Security with either a saml:Assertion or an unprefixed jwt child)
and the SOAP 1.1 faultcode/subcode mapping are exactly the integration details this
project pins, so the tool that government shops actually use holds them.
