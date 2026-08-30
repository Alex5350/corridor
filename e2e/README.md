# Corridor e2e suite (Playwright)

Browser-driven end-to-end tests for the Corridor identity migration story: real
Chromium against the real stack, driving the actual login protocols (signed
SAML 2.0 POST profiles, OIDC authorization code with PKCE) through the same
pages a user sees.

## Running

```bash
cd e2e
npm install          # first time only
npx playwright test  # or: npm test
```

Prerequisites: Docker (for SQL Server when none is running), the .NET 10 SDK,
and Node 22 (the SPA dev server is started with npm). Free contract ports:
1433, 8080, 8090, 8000, 5200, 5173.

## Boot behavior (self-bootstrapping, no webServer)

There is no `webServer` in `playwright.config.ts`. Instead, `global-setup.ts`
boots the whole stack the same way the integration fixture
(`tests/Corridor.IntegrationTests/Infrastructure/CorridorStackFixture.cs`)
does:

1. **Reuse first.** Anything already healthy on a contract port (a stack from
   `scripts/dev-up.sh`, for example) is adopted and reported; teardown never
   stops it. A port that answers but is not healthy aborts the run with a
   clear message instead of fighting it.
2. **Database.** A SQL Server already accepting logins on `localhost:1433` is
   reused; otherwise the compose db service is started with
   `docker compose --profile ci up -d --wait db`. The idempotent
   `db/sql` scripts (schemas, trace procs, seed) are then re-applied over TDS
   (azure-sql-edge ships no sqlcmd, so the GO-separated batches are executed
   directly, exactly like the fixture).
3. **Services.** The four .NET services start as `dotnet run --no-launch-profile`
   with explicit `ASPNETCORE_URLS` and `ConnectionStrings__Corridor`, each
   waited on via its `/healthz` endpoint: okta-sim on 8080, adfs-sim on 8090,
   TraceLink on 8000, the portal on 5200.
4. **SPA.** The FieldInsight dev server starts with `npm run dev` (vite,
   strict port 5173) and is waited on with an HTTP GET.

`global-teardown.ts` kills only the process groups this run started, stops the
db container when this run started it (the named volume survives), and
restores the seeded trust baseline (every app back in Adfs mode). Service logs
land in `$TMPDIR/corridor-e2e-logs/`.

## Spec order and shared state

Specs run in one worker, in file order (alphabetical). Each spec arranges its
own trust mode up front via SQL (like the integration suite's setup helper)
and restores the baseline afterwards, so any single spec can also be run
alone; the order below is just the narrative order:

1. `adfs-login.spec.ts` - portal in Adfs mode: sign-in redirects to the
   adfs-sim forms login, and submitting the demo user returns to the portal
   signed in, with the account shown in the header and the adfs provider
   marker on the home card.
2. `case-lifecycle.spec.ts` - as the officer on the Cases page: create a trace
   request through the REST-to-SOAP bridge, see the new case number, walk
   Received to UnderReview to Traced to Closed, then attempt the illegal
   Closed to UnderReview move and see the stored procedure's transition-guard
   message surfaced in the page.
3. `dual-trust.spec.ts` - portal flipped to Dual via SQL: the chooser shows
   both providers, and both the ADFS and the Okta path complete a real
   sign-in; sign-out works and the mode is untouched.
4. `migration-dashboard.spec.ts` - as the admin: the dashboard lists all three
   apps; flipping legacy Adfs to Dual to Okta through the real audited button
   updates the table, and the audit page (plus the idn.AuditEvents table)
   records the TrustModeChanged events naming the admin. Non-admins get
   access denied.
5. `okta-login.spec.ts` - portal flipped to Okta via SQL: sign-in goes to the
   okta-sim login form, submitting returns signed in with the okta provider
   marker, a wrong password is refused, and the portal flips back to Adfs
   afterwards.
6. `spa-inspector.spec.ts` - the FieldInsight SPA at 5173: PKCE login as the
   inspector (login_hint from the gate), the seeded assignments render with
   progress rings and status pills, a toggled checklist item persists across a
   reload, and the profile card shows the okta-sim claims.

## Known upstream gaps the suite works around

Both are defects in `src/` (out of the e2e suite's write scope); the workarounds
are documented in the specs and in `lib/cors-shim.mjs`:

- **okta-sim sends no CORS headers.** A browser page on the SPA origin cannot
  fetch okta-sim's OIDC endpoints, so the SPA spec installs `lib/cors-shim.mjs`
  on its browser context: it adds the missing Access-Control-Allow-* response
  headers and answers preflights. Every request still reaches the real
  okta-sim; no response body is synthesized. The shim also coalesces the
  duplicate /token POST that React StrictMode's double-invoked callback effect
  produces under the dev server, which would otherwise race the single-use
  authorization code and fail the sign-in about half the time.
- **The Admin > Audit page 500s against SQL Server.**
  `SqlAuditEventRepository.ListRecentAsync` reads the INT `Id` column with
  `GetInt64`, which throws; the page only works in the in-memory build. The
  migration dashboard spec therefore asserts the `idn.AuditEvents` rows (the
  exact rows that page renders) directly in the database, gated on the
  pre-flip audit Id so reruns stay honest.

Demo accounts (synthetic, password `Demo1234!` everywhere):
admin, inspector, officer, and clerk at `corridor.example`.

## Marketing screenshots

The same bootstrap also powers the README screenshot capture (real Chromium,
1366x900 at deviceScaleFactor 2):

```bash
npm run screenshots   # writes docs/screenshots/*.png
```
