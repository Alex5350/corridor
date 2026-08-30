Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports System.Security.Cryptography
Imports System.Text

' Pure RS256 validation against a JWKS document: no HTTP and no clock reads
' (the caller passes the moment to evaluate), so tests are deterministic.
Public Module TokenValidator

    ' One validation check with its outcome and an operator readable detail.
    Public NotInheritable Class CheckResult
        Public Sub New(checkName As String, checkPassed As Boolean, detail As String)
            Me.Name = checkName
            Me.Passed = checkPassed
            Me.Detail = detail
        End Sub

        Public ReadOnly Property Name As String
        Public ReadOnly Property Passed As Boolean
        Public ReadOnly Property Detail As String

        Public Overrides Function ToString() As String
            Return Name & ": " & If(Passed, "PASS", "FAIL") & " " & Detail
        End Function
    End Class

    ' All checks for one token; AllPassed is the overall verdict.
    Public NotInheritable Class ValidationResult
        Public Sub New(checkResults As IReadOnlyList(Of CheckResult))
            Me.Checks = checkResults
        End Sub

        Public ReadOnly Property Checks As IReadOnlyList(Of CheckResult)

        Public ReadOnly Property AllPassed As Boolean
            Get
                Return Checks.All(Function(checkItem) checkItem.Passed)
            End Get
        End Property
    End Class

    ' Validates structure, algorithm, key lookup, RS256 signature, issuer,
    ' audience, expiry, and not-before. Skipped issuer or audience checks
    ' (no expectation supplied) count as passed with a note.
    Public Function Validate(token As String,
                             jwksJson As String,
                             expectedIssuer As String,
                             expectedAudience As String,
                             now As DateTimeOffset) As ValidationResult
        Dim checks = New List(Of CheckResult)

        Dim outcome = TokenDecoder.TryDecode(token)
        If Not outcome.Ok Then
            checks.Add(New CheckResult("structure", False, outcome.ErrorMessage))
            Return New ValidationResult(checks)
        End If

        Dim decoded = outcome.Decoded
        checks.Add(New CheckResult("structure", True, "header and payload decoded"))

        Dim algorithm As String = Nothing
        decoded.Header.TryGetValue("alg", algorithm)
        checks.Add(New CheckResult("algorithm",
                                   String.Equals(algorithm, "RS256", StringComparison.Ordinal),
                                   "header alg is " & If(algorithm, "(missing)")))

        Dim kid As String = Nothing
        decoded.Header.TryGetValue("kid", kid)
        Dim matchingKey = FindKey(decoded.Header, jwksJson)
        checks.Add(New CheckResult("key",
                                   matchingKey IsNot Nothing,
                                   "kid " & If(kid, "(missing)") &
                                   If(matchingKey IsNot Nothing, " found in JWKS", " not found in JWKS")))

        checks.Add(SignatureCheck(decoded, matchingKey))
        checks.Add(IssuerCheck(decoded.Payload, expectedIssuer))
        checks.Add(AudienceCheck(decoded.Payload, expectedAudience))
        checks.Add(ExpiryCheck(decoded.Payload, now))
        checks.Add(NotBeforeCheck(decoded.Payload, now))

        Return New ValidationResult(checks)
    End Function

    Private Function FindKey(header As IReadOnlyDictionary(Of String, String),
                             jwksJson As String) As JwkKeyRecord
        Dim kid As String = Nothing
        If Not header.TryGetValue("kid", kid) OrElse String.IsNullOrEmpty(kid) Then
            Return Nothing
        End If

        For Each keyRecord In MetadataParser.ParseJwksKeys(jwksJson)
            If IsUsableRsaKey(keyRecord) AndAlso String.Equals(keyRecord.Kid, kid, StringComparison.Ordinal) Then
                Return keyRecord
            End If
        Next
        Return Nothing
    End Function

    ' A JWKS entry usable for RS256: an RSA key with kid, modulus, exponent.
    Private Function IsUsableRsaKey(keyRecord As JwkKeyRecord) As Boolean
        Return String.Equals(keyRecord.KeyType, "RSA", StringComparison.Ordinal) _
            AndAlso Not String.IsNullOrEmpty(keyRecord.Kid) _
            AndAlso Not String.IsNullOrEmpty(keyRecord.Modulus) _
            AndAlso Not String.IsNullOrEmpty(keyRecord.Exponent)
    End Function

    Private Function SignatureCheck(decoded As TokenDecoder.DecodedJwt,
                                    keyRecord As JwkKeyRecord) As CheckResult
        If keyRecord Is Nothing Then
            Return New CheckResult("signature", False, "skipped: no matching key in JWKS")
        End If

        Try
            Using rsaKey = RSA.Create()
                rsaKey.ImportParameters(New RSAParameters With {
                    .Modulus = TokenDecoder.Base64UrlDecode(keyRecord.Modulus),
                    .Exponent = TokenDecoder.Base64UrlDecode(keyRecord.Exponent)
                })
                Dim signingInput = decoded.HeaderSegment & "." & decoded.PayloadSegment
                Dim signatureBytes = TokenDecoder.Base64UrlDecode(decoded.SignatureSegment)
                Dim verified = rsaKey.VerifyData(Encoding.ASCII.GetBytes(signingInput),
                                              signatureBytes,
                                              HashAlgorithmName.SHA256,
                                              RSASignaturePadding.Pkcs1)
                Return New CheckResult("signature",
                                       verified,
                                       If(verified, "RS256 signature verified",
                                          "RS256 signature does not match the token"))
            End Using
        Catch ex As FormatException
            Return New CheckResult("signature", False, "signature segment or key material is malformed: " & ex.Message)
        Catch ex As CryptographicException
            Return New CheckResult("signature", False, "key material rejected by the crypto provider: " & ex.Message)
        Catch ex As ArgumentException
            Return New CheckResult("signature", False, "key material is malformed: " & ex.Message)
        End Try
    End Function

    Private Function IssuerCheck(payload As IReadOnlyDictionary(Of String, String),
                                 expectedIssuer As String) As CheckResult
        Dim claimValue As String = Nothing
        payload.TryGetValue(TokenDecoder.IssuerClaim, claimValue)
        If String.IsNullOrEmpty(expectedIssuer) Then
            Return New CheckResult("issuer", True,
                                   "skipped: no --iss supplied (token iss is " & If(claimValue, "(missing)") & ")")
        End If
        Dim matched = String.Equals(claimValue, expectedIssuer, StringComparison.Ordinal)
        Return New CheckResult("issuer", matched,
                               "token iss is " & If(claimValue, "(missing)") & ", expected " & expectedIssuer)
    End Function

    Private Function AudienceCheck(payload As IReadOnlyDictionary(Of String, String),
                                   expectedAudience As String) As CheckResult
        Dim claimValue As String = Nothing
        payload.TryGetValue(TokenDecoder.AudienceClaim, claimValue)
        If String.IsNullOrEmpty(expectedAudience) Then
            Return New CheckResult("audience", True,
                                   "skipped: no --aud supplied (token aud is " & If(claimValue, "(missing)") & ")")
        End If
        Dim matched = AudienceMatches(claimValue, expectedAudience)
        Return New CheckResult("audience", matched,
                               "token aud is " & If(claimValue, "(missing)") & ", expected " & expectedAudience)
    End Function

    ' aud may be a single string or a list; the decoder flattens lists to
    ' comma separated text, so split before comparing.
    Private Function AudienceMatches(audienceClaim As String, expected As String) As Boolean
        If audienceClaim Is Nothing Then
            Return False
        End If
        For Each candidate In audienceClaim.Split(New String() {", "}, StringSplitOptions.None)
            If String.Equals(candidate.Trim(), expected, StringComparison.Ordinal) Then
                Return True
            End If
        Next
        Return False
    End Function

    Private Function ExpiryCheck(payload As IReadOnlyDictionary(Of String, String),
                                 now As DateTimeOffset) As CheckResult
        Dim expiresAt As DateTimeOffset
        If Not TokenDecoder.TryGetUnixTime(payload, TokenDecoder.ExpiresClaim, expiresAt) Then
            Return New CheckResult("expiry", False, "token carries no exp claim")
        End If
        Dim stillValid = now < expiresAt
        Return New CheckResult("expiry", stillValid,
                               If(stillValid, "expires ", "expired at ") &
                               TokenDecoder.FormatLocal(expiresAt) & " local time")
    End Function

    Private Function NotBeforeCheck(payload As IReadOnlyDictionary(Of String, String),
                                    now As DateTimeOffset) As CheckResult
        Dim notBefore As DateTimeOffset
        If Not TokenDecoder.TryGetUnixTime(payload, TokenDecoder.NotBeforeClaim, notBefore) Then
            Return New CheckResult("not-before", True, "skipped: token carries no nbf claim")
        End If
        Dim validFromNow = now >= notBefore
        Return New CheckResult("not-before", validFromNow,
                               If(validFromNow, "valid from ", "not valid before ") &
                               TokenDecoder.FormatLocal(notBefore) & " local time")
    End Function
End Module
