Imports System
Imports System.Collections.Generic
Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Net.Http
Imports System.Text.Json
Imports System.Threading.Tasks
Imports System.Xml.Linq

' Sub command implementations. Each Run function parses its own arguments,
' prints a report, and returns the process exit code. Fetches go through
' SharedHttp (5 s timeout) and block on GetAwaiter, which is fine for a
' short lived console app.
Public Module Commands

    Public Function RunCheckMetadata(args As String()) As Integer
        Dim parsed = CommandLine.Parse(args)
        If IsHelpRequested(parsed) Then
            Output.WriteLine(HelpText.CheckMetadataHelp())
            Return ExitCodes.Success
        End If

        Dim idp = parsed.GetOption("idp")
        If idp Is Nothing Then
            Output.Fail("check-metadata requires --idp adfs or --idp okta")
            Output.WriteLine(HelpText.CheckMetadataHelp())
            Return ExitCodes.Usage
        End If

        Dim defaultUrl As String
        Select Case idp.ToLowerInvariant()
            Case "adfs"
                defaultUrl = "http://localhost:8090" & MetadataParser.AdfsMetadataPath
            Case "okta"
                defaultUrl = "http://localhost:8080" & MetadataParser.OktaDiscoveryPath
            Case Else
                Output.Fail("unknown idp '" & idp & "', expected adfs or okta")
                Return ExitCodes.Usage
        End Select

        Dim url = parsed.GetOption("url", defaultUrl)
        Output.WriteLine("provider       : " & idp.ToLowerInvariant())
        Output.WriteLine("metadata url   : " & url)

        Try
            Dim body = FetchText(url)
            If String.Equals(idp, "adfs", StringComparison.OrdinalIgnoreCase) Then
                Return ReportAdfs(body)
            End If
            Return ReportOkta(body)
        Catch ex As Exception
            Return ReportMetadataFailure(ex, url)
        End Try
    End Function

    Public Function RunDecodeToken(args As String()) As Integer
        Dim parsed = CommandLine.Parse(args)
        If IsHelpRequested(parsed) Then
            Output.WriteLine(HelpText.DecodeTokenHelp())
            Return ExitCodes.Success
        End If
        If parsed.Positional.Count <> 1 Then
            Output.Fail("decode-token takes exactly one token argument")
            Output.WriteLine(HelpText.DecodeTokenHelp())
            Return ExitCodes.Usage
        End If

        Dim outcome = TokenDecoder.TryDecode(parsed.Positional(0))
        If Not outcome.Ok Then
            Output.Fail("token could not be decoded: " & outcome.ErrorMessage)
            Return ExitCodes.InvalidToken
        End If

        Dim decoded = outcome.Decoded
        Output.WriteLine("header", Output.AnsiColor.Cyan)
        Output.WriteLine(RenderClaimsTable(decoded.Header))
        Output.WriteLine("payload", Output.AnsiColor.Cyan)
        Output.WriteLine(RenderClaimsTable(decoded.Payload))

        Dim warnings = TokenDecoder.TimeWarnings(decoded.Payload, DateTimeOffset.Now)
        For Each warningLine In warnings
            Output.Warn(warningLine)
        Next
        If warnings.Count = 0 Then
            Output.Pass("token sits inside its validity window")
        End If
        Return ExitCodes.Success
    End Function

    Public Function RunValidateToken(args As String()) As Integer
        Dim parsed = CommandLine.Parse(args)
        If IsHelpRequested(parsed) Then
            Output.WriteLine(HelpText.ValidateTokenHelp())
            Return ExitCodes.Success
        End If
        If parsed.Positional.Count <> 1 Then
            Output.Fail("validate-token takes exactly one token argument")
            Output.WriteLine(HelpText.ValidateTokenHelp())
            Return ExitCodes.Usage
        End If

        Dim jwksSource = parsed.GetOption("jwks")
        If jwksSource Is Nothing Then
            Output.Fail("validate-token requires --jwks <url or file path>")
            Output.WriteLine(HelpText.ValidateTokenHelp())
            Return ExitCodes.Usage
        End If

        Dim jwksJson = LoadJwks(jwksSource)
        If jwksJson Is Nothing Then
            Return ExitCodes.InvalidToken
        End If

        Dim result = TokenValidator.Validate(parsed.Positional(0),
                                             jwksJson,
                                             parsed.GetOption("iss"),
                                             parsed.GetOption("aud"),
                                             DateTimeOffset.Now)
        For Each check In result.Checks
            Dim marker = If(check.Passed, "PASS", "FAIL")
            Output.WriteLine(check.Name.PadRight(12) & marker.PadRight(6) & check.Detail,
                             If(check.Passed, Output.AnsiColor.Green, Output.AnsiColor.Red))
        Next

        If result.AllPassed Then
            Output.Pass("token is valid")
            Return ExitCodes.Success
        End If
        Output.Fail("token validation failed")
        Return ExitCodes.InvalidToken
    End Function

    Public Function RunScimDump(args As String()) As Integer
        Dim parsed = CommandLine.Parse(args)
        If IsHelpRequested(parsed) Then
            Output.WriteLine(HelpText.ScimDumpHelp())
            Return ExitCodes.Success
        End If

        Dim baseUrl = parsed.GetOption("url")
        Dim token = parsed.GetOption("token")
        If baseUrl Is Nothing OrElse token Is Nothing Then
            Output.Fail("scim-dump requires --url and --token")
            Output.WriteLine(HelpText.ScimDumpHelp())
            Return ExitCodes.Usage
        End If

        Dim fullUrl = ScimDump.BuildUrl(baseUrl)
        Try
            Dim body = ScimDump.FetchAsync(SharedHttp.Client, fullUrl, token).GetAwaiter().GetResult()
            Dim users = ScimDump.ParseUsers(body)
            Output.WriteLine("scim endpoint  : " & fullUrl)
            Output.WriteLine(ScimDump.RenderTable(users))
            Output.Pass(users.Count.ToString(CultureInfo.InvariantCulture) & " user(s) listed")
            Return ExitCodes.Success
        Catch ex As ScimRequestException
            Output.Fail("scim request failed: " & ex.Message)
            Return ExitCodes.ScimError
        Catch ex As HttpRequestException
            Output.Fail("scim endpoint unreachable: " & ex.Message)
            Return ExitCodes.ScimError
        Catch ex As TaskCanceledException
            Output.Fail("scim endpoint timed out after 5 s: " & fullUrl)
            Return ExitCodes.ScimError
        Catch ex As JsonException
            Output.Fail("scim response is not valid JSON: " & ex.Message)
            Return ExitCodes.ScimError
        End Try
    End Function

    Public Function RunWhoAmI(args As String()) As Integer
        Dim parsed = CommandLine.Parse(args)
        If IsHelpRequested(parsed) Then
            Output.WriteLine(HelpText.WhoAmITokenHelp())
            Return ExitCodes.Success
        End If
        If parsed.Positional.Count <> 1 Then
            Output.Fail("whoami-token takes exactly one token argument")
            Output.WriteLine(HelpText.WhoAmITokenHelp())
            Return ExitCodes.Usage
        End If

        Dim outcome = TokenDecoder.TryDecode(parsed.Positional(0))
        If Not outcome.Ok Then
            Output.Fail("token could not be decoded: " & outcome.ErrorMessage)
            Return ExitCodes.InvalidToken
        End If

        Dim payload = outcome.Decoded.Payload
        Dim upn = FirstClaim(payload, "upn", "preferred_username", "sub")
        Dim displayedUpn = If(String.IsNullOrEmpty(upn), "(no upn claim)", upn)
        Dim roles = RolesFrom(payload)
        Dim expiresAt As DateTimeOffset
        Dim hasExpiry = TokenDecoder.TryGetUnixTime(payload, TokenDecoder.ExpiresClaim, expiresAt)

        ' XML literal summary: upn attribute, one role element per role claim.
        Dim summary = <whoami upn=<%= displayedUpn %>><roles/></whoami>
        Dim rolesElement = summary.Element("roles")
        For Each role In roles
            rolesElement.Add(<role><%= role %></role>)
        Next
        If hasExpiry Then
            summary.Add(<expires local=<%= TokenDecoder.FormatLocal(expiresAt) %>/>)
        End If
        Output.WriteLine(summary.ToString())

        For Each warningLine In TokenDecoder.TimeWarnings(payload, DateTimeOffset.Now)
            Output.Warn(warningLine)
        Next
        Return ExitCodes.Success
    End Function

    ' True when the operator asked for the per command help screen.
    Private Function IsHelpRequested(parsed As ParsedArguments) As Boolean
        Return parsed.HasOption("help") OrElse parsed.HasOption("h")
    End Function

    Private Function FetchText(url As String) As String
        Return SharedHttp.Client.GetStringAsync(url).GetAwaiter().GetResult()
    End Function

    Private Function ReportAdfs(body As String) As Integer
        Dim metadata = MetadataParser.ParseAdfsMetadata(body)
        Output.WriteLine("entity id      : " & metadata.EntityId)
        Output.WriteLine("sso endpoint   : " & metadata.SingleSignOnEndpoint)
        Output.WriteLine("binding        : " & metadata.Binding)
        Output.WriteLine("thumbprint     : " & metadata.CertificateThumbprint)
        Output.WriteLine("cert subject   : " & metadata.CertificateSubject)
        Output.WriteLine("cert not after : " & TokenDecoder.FormatLocal(metadata.CertificateNotAfter))
        Output.Pass("metadata OK: well formed, parse safe (DTD prohibited)")
        Return ExitCodes.Success
    End Function

    Private Function ReportOkta(body As String) As Integer
        Dim discovery = MetadataParser.ParseDiscovery(body)
        Output.WriteLine("issuer         : " & discovery.Issuer)
        If Not String.IsNullOrEmpty(discovery.AuthorizationEndpoint) Then
            Output.WriteLine("authorize ep   : " & discovery.AuthorizationEndpoint)
        End If
        If Not String.IsNullOrEmpty(discovery.TokenEndpoint) Then
            Output.WriteLine("token ep       : " & discovery.TokenEndpoint)
        End If
        Output.WriteLine("jwks uri       : " & discovery.JwksUri)

        ' The JWKS hop is secondary: warn on failure, keep the metadata verdict.
        Try
            Dim jwksBody = FetchText(discovery.JwksUri)
            Dim kids = MetadataParser.ParseJwksKids(jwksBody)
            Output.WriteLine("jwks key ids   : " &
                             If(kids.Count = 0, "(none published)", String.Join(", ", kids)))
        Catch ex As Exception
            Output.Warn("could not load the JWKS (" & ex.GetType().Name & ": " & ex.Message & ")")
        End Try

        Output.Pass("metadata OK: discovery document parsed")
        Return ExitCodes.Success
    End Function

    Private Function ReportMetadataFailure(ex As Exception, url As String) As Integer
        Dim code = ExitCodes.ForMetadataFailure(ex)
        If code = ExitCodes.Unreachable Then
            Output.Fail("could not reach " & url & ": " & ex.Message)
        ElseIf code = ExitCodes.InvalidMetadata Then
            Output.Fail("metadata from " & url & " is invalid: " & ex.Message)
        Else
            Output.Fail("metadata check failed: " & ex.Message)
        End If
        Return code
    End Function

    ' Loads a JWKS from an http(s) url or a local file; Nothing means the
    ' failure was already reported.
    Private Function LoadJwks(source As String) As String
        If source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) OrElse
           source.StartsWith("https://", StringComparison.OrdinalIgnoreCase) Then
            Try
                Return FetchText(source)
            Catch ex As Exception
                Output.Fail("could not load JWKS from " & source & ": " & ex.Message)
                Return Nothing
            End Try
        End If

        Try
            Return File.ReadAllText(source)
        Catch ex As Exception
            Output.Fail("could not read JWKS file " & source & ": " & ex.Message)
            Return Nothing
        End Try
    End Function

    ' Two column claim table; unix time claims also render their local time.
    Private Function RenderClaimsTable(claims As IReadOnlyDictionary(Of String, String)) As String
        Dim table = New TextTable({"claim", "value"}, {14, 64})
        For Each claimName In claims.Keys
            table.AddRow(claimName, LocalizedClaimValue(claimName, claims(claimName)))
        Next
        Return table.Render()
    End Function

    Private Function LocalizedClaimValue(claimName As String, value As String) As String
        Dim seconds As Long
        If Not IsTimeClaim(claimName) OrElse
           Not Long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, seconds) Then
            Return value
        End If
        Dim instant = DateTimeOffset.FromUnixTimeSeconds(seconds)
        Return value & " (" & TokenDecoder.FormatLocal(instant) & " local)"
    End Function

    Private Function IsTimeClaim(claimName As String) As Boolean
        Select Case claimName.ToLowerInvariant()
            Case "exp", "nbf", "iat", "auth_time"
                Return True
            Case Else
                Return False
        End Select
    End Function

    ' First present claim among the candidates, case insensitive.
    Private Function FirstClaim(claims As IReadOnlyDictionary(Of String, String),
                                ParamArray names As String()) As String
        For Each name In names
            Dim value As String = Nothing
            If claims.TryGetValue(name, value) Then
                Return value
            End If
        Next
        Return Nothing
    End Function

    ' Collects the role claim (falling back to groups), split into items.
    Private Function RolesFrom(payload As IReadOnlyDictionary(Of String, String)) As List(Of String)
        Dim flatText = FirstClaim(payload, "role", "roles", "groups")
        Dim roles = New List(Of String)
        If Not String.IsNullOrEmpty(flatText) Then
            For Each item In flatText.Split(New String() {", "}, StringSplitOptions.RemoveEmptyEntries)
                Dim trimmed = item.Trim()
                If trimmed.Length > 0 Then
                    roles.Add(trimmed)
                End If
            Next
        End If
        If roles.Count = 0 Then
            roles.Add("(no role claim)")
        End If
        Return roles
    End Function
End Module
