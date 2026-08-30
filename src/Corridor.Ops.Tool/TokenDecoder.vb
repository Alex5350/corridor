Imports System
Imports System.Collections.Generic
Imports System.Globalization
Imports System.Text
Imports System.Text.Json

' Pure JWT decoding: splits the token, base64url-decodes the header and
' payload, and flattens the JSON claims into string dictionaries. There is no
' network access and no signature checking here; validate-token owns that.
Public Module TokenDecoder

    ' Claim names the tool treats specially.
    Public Const ExpiresClaim As String = "exp"
    Public Const NotBeforeClaim As String = "nbf"
    Public Const IssuerClaim As String = "iss"
    Public Const AudienceClaim As String = "aud"
    Public Const UpnClaim As String = "upn"
    Public Const RoleClaim As String = "role"

    ' A decoded token: flattened header and payload claims plus the raw
    ' segments, because the validator rebuilds the signing input from them.
    Public NotInheritable Class DecodedJwt
        Public Sub New(headerClaims As IReadOnlyDictionary(Of String, String),
                       payloadClaims As IReadOnlyDictionary(Of String, String),
                       headerSegment As String,
                       payloadSegment As String,
                       signatureSegment As String)
            Me.Header = headerClaims
            Me.Payload = payloadClaims
            Me.HeaderSegment = headerSegment
            Me.PayloadSegment = payloadSegment
            Me.SignatureSegment = signatureSegment
        End Sub

        Public ReadOnly Property Header As IReadOnlyDictionary(Of String, String)
        Public ReadOnly Property Payload As IReadOnlyDictionary(Of String, String)
        Public ReadOnly Property HeaderSegment As String
        Public ReadOnly Property PayloadSegment As String
        Public ReadOnly Property SignatureSegment As String
    End Class

    ' Result of a decode attempt: Decoded is Nothing unless Ok is True.
    Public NotInheritable Class DecodeOutcome
        Public Sub New(decodedJwt As DecodedJwt, errorMessage As String)
            Me.Decoded = decodedJwt
            Me.ErrorMessage = errorMessage
        End Sub

        Public ReadOnly Property Decoded As DecodedJwt
        Public ReadOnly Property ErrorMessage As String

        Public ReadOnly Property Ok As Boolean
            Get
                Return Decoded IsNot Nothing
            End Get
        End Property
    End Class

    ' Decodes header and payload without validating anything. Accepts two
    ' segments (unsigned preview) or three segments (JWS); anything else fails.
    Public Function TryDecode(token As String) As DecodeOutcome
        If String.IsNullOrWhiteSpace(token) Then
            Return FailedOutcome("token is empty")
        End If

        Dim parts = token.Split("."c)
        If parts.Length < 2 OrElse parts.Length > 3 Then
            Return FailedOutcome("expected two or three dot separated segments, found " &
                                 parts.Length.ToString(CultureInfo.InvariantCulture))
        End If

        Try
            Dim headerJson = Encoding.UTF8.GetString(Base64UrlDecode(parts(0)))
            Dim payloadJson = Encoding.UTF8.GetString(Base64UrlDecode(parts(1)))
            Dim signatureSegment = If(parts.Length = 3, parts(2), "")
            Return New DecodeOutcome(
                New DecodedJwt(ParseClaims(headerJson), ParseClaims(payloadJson),
                               parts(0), parts(1), signatureSegment),
                Nothing)
        Catch ex As FormatException
            Return FailedOutcome("segment could not be decoded: " & ex.Message)
        Catch ex As JsonException
            Return FailedOutcome("segment is not valid JSON: " & ex.Message)
        Catch ex As ArgumentException
            Return FailedOutcome(ex.Message)
        End Try
    End Function

    ' Decodes base64url text (RFC 4648 section 5). Tolerates padding some
    ' encoders leave in and restores the padding most JWT encoders drop.
    Public Function Base64UrlDecode(text As String) As Byte()
        If text Is Nothing Then
            Throw New FormatException("base64url segment is missing")
        End If

        Dim builder = New StringBuilder(text.Length)
        For Each character As Char In text
            Select Case character
                Case "-"c
                    builder.Append("+"c)
                Case "_"c
                    builder.Append("/"c)
                Case Else
                    builder.Append(character)
            End Select
        Next

        Dim normalized = builder.ToString().TrimEnd("="c)
        Select Case normalized.Length Mod 4
            Case 0
                ' Already aligned.
            Case 2
                normalized &= "=="
            Case 3
                normalized &= "="
            Case Else
                Throw New FormatException("base64url segment has an invalid length")
        End Select
        Return Convert.FromBase64String(normalized)
    End Function

    ' Warning lines for exp and nbf at the given moment; an empty list means
    ' the token sits inside its validity window.
    Public Function TimeWarnings(payload As IReadOnlyDictionary(Of String, String),
                                 now As DateTimeOffset) As IReadOnlyList(Of String)
        Dim warnings = New List(Of String)

        Dim expiresAt As DateTimeOffset
        If TryGetUnixTime(payload, ExpiresClaim, expiresAt) AndAlso now >= expiresAt Then
            warnings.Add("EXPIRES: token expired at " & FormatLocal(expiresAt))
        End If

        Dim notBefore As DateTimeOffset
        If TryGetUnixTime(payload, NotBeforeClaim, notBefore) AndAlso now < notBefore Then
            warnings.Add("NOT-YET-VALID: token is not valid before " & FormatLocal(notBefore))
        End If

        Return warnings
    End Function

    ' Reads a numeric unix time claim; False when it is missing or not a number.
    Public Function TryGetUnixTime(payload As IReadOnlyDictionary(Of String, String),
                                   claimName As String,
                                   ByRef value As DateTimeOffset) As Boolean
        Dim raw As String = Nothing
        If Not payload.TryGetValue(claimName, raw) Then
            Return False
        End If

        Dim seconds As Long
        If raw Is Nothing OrElse Not Long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, seconds) Then
            Return False
        End If

        value = DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime()
        Return True
    End Function

    ' Formats an instant as local time with an explicit offset.
    Public Function FormatLocal(instant As DateTimeOffset) As String
        Return instant.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)
    End Function

    Private Function FailedOutcome(message As String) As DecodeOutcome
        Return New DecodeOutcome(Nothing, message)
    End Function

    ' Flattens a claims JSON object; arrays become comma separated text so the
    ' table rendering stays on one line.
    Private Function ParseClaims(json As String) As IReadOnlyDictionary(Of String, String)
        Dim claims = New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        Using document = JsonDocument.Parse(json)
            If document.RootElement.ValueKind <> JsonValueKind.Object Then
                Throw New FormatException("claims segment is not a JSON object")
            End If
            For Each claimProperty As JsonProperty In document.RootElement.EnumerateObject()
                claims(claimProperty.Name) = ClaimText(claimProperty.Value)
            Next
        End Using
        Return claims
    End Function

    Private Function ClaimText(value As JsonElement) As String
        Select Case value.ValueKind
            Case JsonValueKind.String
                Return value.GetString()
            Case JsonValueKind.Array
                Dim items = New List(Of String)
                For Each element As JsonElement In value.EnumerateArray()
                    items.Add(ClaimText(element))
                Next
                Return String.Join(", ", items)
            Case JsonValueKind.Null
                Return ""
            Case Else
                Return value.GetRawText()
        End Select
    End Function
End Module
