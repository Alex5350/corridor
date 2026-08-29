# Corridor commit plan (internal build choreography)

All commits carry explicit GIT_AUTHOR_DATE and GIT_COMMITTER_DATE in the Sat 2026-08-29 /
Sun 2026-08-30 evening windows (-0500), dependency ordered. The coordinator creates ALL
commits; build agents never commit.

Wave A (Sat 18:05-19:40): scaffold + contracts + db scripts + okta-sim + adfs-sim
Wave B (Sat 19:45-21:30): legacy SOAP service + portal
Wave C (Sat 21:35-23:30): spa + ops tool + integration tests + postman/soapui/jmeter
Wave D (Sun 18:05-19:45): e2e + deploy artifacts + CI + security findings ledger
Wave E (Sun 19:50-22:40): docs suite (README, TECHNICAL, GLOSSARY, ADRs, plans),
  diagrams (FlowInk renders), screenshots, polish

Message style: conventional, specific, e.g. "feat(oktasim): OIDC authorization-code
flow with PKCE and JWKS rotation". No AI mentions. No em/en dashes in messages.
