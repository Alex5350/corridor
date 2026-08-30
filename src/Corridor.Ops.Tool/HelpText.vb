Imports System

' Every help screen in one module so the wording stays consistent with
' USAGE.md and the exit code table.
Public Module HelpText

    Public Function GeneralHelp() As String
        Dim lines As String() = {
            "corridor-ops: pocket tool for the Corridor identity cutover weekend",
            "",
            "usage:",
            "  corridor-ops <command> [options]",
            "",
            "commands:",
            "  check-metadata    fetch ADFS or Okta federation metadata and sanity check it",
            "  decode-token      decode a JWT and print claims plus time warnings",
            "  validate-token    verify a JWT signature against a JWKS plus iss/aud/exp",
            "  scim-dump         list users from a SCIM 2.0 endpoint as a table",
            "  whoami-token      print an upn and role summary from a JWT",
            "",
            "options:",
            "  --help, -h        show this help (per command: corridor-ops <command> --help)",
            "",
            "environment:",
            "  NO_COLOR          set to any non-empty value to switch off ANSI colors",
            "",
            "exit codes:",
            "  0  success",
            "  1  usage error",
            "  2  invalid metadata",
            "  3  endpoint unreachable (5 s timeout)",
            "  4  invalid or malformed token",
            "  5  scim endpoint error"
        }
        Return String.Join(Environment.NewLine, lines)
    End Function

    Public Function CheckMetadataHelp() As String
        Dim lines As String() = {
            "check-metadata: fetch federation metadata and sanity check it",
            "",
            "usage:",
            "  corridor-ops check-metadata --idp adfs|okta [--url URL]",
            "",
            "options:",
            "  --idp   provider to check: adfs (SAML metadata) or okta (OIDC discovery)",
            "  --url   override the default endpoint",
            "",
            "defaults:",
            "  adfs  http://localhost:8090" & MetadataParser.AdfsMetadataPath,
            "  okta  http://localhost:8080" & MetadataParser.OktaDiscoveryPath,
            "",
            "notes:",
            "  XML is parsed with DTD prohibited; okta also lists the JWKS key ids",
            "exit codes: 0 ok, 2 invalid metadata, 3 unreachable"
        }
        Return String.Join(Environment.NewLine, lines)
    End Function

    Public Function DecodeTokenHelp() As String
        Dim lines As String() = {
            "decode-token: decode a JWT and print header and payload claims",
            "",
            "usage:",
            "  corridor-ops decode-token <jwt>",
            "",
            "notes:",
            "  decoding never validates the signature; use validate-token for that",
            "  exp/nbf/iat/auth_time values also show their local time",
            "  stale tokens raise EXPIRES and NOT-YET-VALID warnings",
            "exit codes: 0 ok, 1 usage, 4 malformed token"
        }
        Return String.Join(Environment.NewLine, lines)
    End Function

    Public Function ValidateTokenHelp() As String
        Dim lines As String() = {
            "validate-token: validate a JWT against a JWKS",
            "",
            "usage:",
            "  corridor-ops validate-token <jwt> --jwks <url|path> [--iss ISSUER] [--aud AUDIENCE]",
            "",
            "options:",
            "  --jwks   JWKS source: an http(s) url or a local file path",
            "  --iss    expected issuer (check skipped when absent)",
            "  --aud    expected audience (check skipped when absent)",
            "",
            "checks: structure, algorithm, key lookup, RS256 signature,",
            "        issuer, audience, expiry, not-before",
            "exit codes: 0 all checks pass, 4 any check fails"
        }
        Return String.Join(Environment.NewLine, lines)
    End Function

    Public Function ScimDumpHelp() As String
        Dim lines As String() = {
            "scim-dump: list SCIM 2.0 users as a table",
            "",
            "usage:",
            "  corridor-ops scim-dump --url URL --token TOKEN",
            "",
            "options:",
            "  --url    base url of the SCIM service (the " & ScimDump.UsersPath &
                   " path is appended) or the full endpoint url",
            "  --token  bearer token, sent but never printed",
            "",
            "example:",
            "  corridor-ops scim-dump --url http://localhost:8080 --token corridor-scim-token",
            "exit codes: 0 ok, 5 scim endpoint error"
        }
        Return String.Join(Environment.NewLine, lines)
    End Function

    Public Function WhoAmITokenHelp() As String
        Dim lines As String() = {
            "whoami-token: summarize upn and roles from a JWT",
            "",
            "usage:",
            "  corridor-ops whoami-token <jwt>",
            "",
            "notes:",
            "  decodes without validating; upn falls back to preferred_username, then sub",
            "  roles come from the role claim, falling back to groups",
            "  prints an XML summary plus EXPIRES / NOT-YET-VALID warnings",
            "exit codes: 0 ok, 1 usage, 4 malformed token"
        }
        Return String.Join(Environment.NewLine, lines)
    End Function
End Module
