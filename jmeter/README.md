# JMeter: Corridor flows

`corridor-flow.jmx` drives two flows against the local demo stack:

- **Portal read path**: 4 virtual users (one per row of `users.csv`) each perform a
  real portal sign-in through okta-sim's OIDC login form (the plan scrapes the hidden
  authorize fields, PKCE included, and posts the credentials), then loop 50 times over
  `GET /permits` and `GET /api/cases` with response-code and JSON assertions.
- **SCIM writes**: 2 threads x 10 iterations of SCIM create + deactivate against
  `http://localhost:8080/scim/v2/Users` with the demo bearer token, asserting the
  `application/scim+json` round trip (201 then 200, active flipped to false).

Everything here is synthetic demo data; the demo password and the SCIM token are the
documented constants from the architecture contract, never real secrets.

## Prerequisites

1. The stack is up: SQL Server (compose `db` service or `tests/Corridor.IntegrationTests`'s
   container) plus okta-sim (8080) and the portal (5200). The contract ports are in
   `docs/contracts/ARCHITECTURE-CONTRACT.md`.
2. The portal must accept Okta sign-in: flip it to Dual or Okta trust mode on the
   portal's Admin > Migration dashboard (or with SQL:
   `UPDATE idn.MigrationApps SET TrustMode='Dual' WHERE AppKey='portal';`). The seeded
   default is Adfs, which routes logins to adfs-sim instead.
3. The cookie manager uses the `compatibility` policy on purpose: the portal's OIDC
   correlation cookie is issued with the Secure attribute, and the strict RFC 6265
   policies refuse to replay Secure cookies over plain http. Browsers treat
   http://localhost as a secure context, so real users are unaffected.
3. The `/api/cases` reads delegate to the legacy SOAP service, so the legacy app
   (port 8000) must be running too.

## Run headless

From this directory (so `users.csv` resolves):

```
jmeter -n -t corridor-flow.jmx -l results.jtl
```

With the HTML dashboard (off by default in this plan; `-e` opts in):

```
jmeter -n -t corridor-flow.jmx -l results.jtl -e -o report/
```

Then open `report/index.html`. Drop `-e -o report/` for plain runs; headless JMeter
still prints periodic `summary +` aggregate lines to the console, and the plan's
Summary Report listener aggregates the same samples when you open it in the GUI.

## What to look for

- **Error % in the Summary Report** should stay at 0. Any login failures usually mean
  the portal trust mode is still Adfs (see Prerequisites) or the stack is down.
- **Average and 95th percentile** for `GET /api/cases` reflect the full
  portal -> SOAP -> SQL round trip, so they run noticeably higher than `GET /permits`
  (portal + SQL only). That gap is the REST-to-SOAP bridge cost, a useful demo number.
- **Throughput** of the SCIM writes thread group: 20 created-then-deactivated users
  per run, visible in `idn.Users` afterwards.
- In the dashboard, the response-time percentiles over time should be flat; spikes at
  the very start are the four concurrent OIDC logins (JIT token exchanges).

## Files

- `corridor-flow.jmx` - the test plan (JMeter 5.x).
- `users.csv` - the four synthetic demo users and the shared demo password.
