Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports System.Security.Cryptography
Imports System.Security.Cryptography.X509Certificates
Imports System.Text.Json
Imports System.Xml
Imports System.Xml.Linq

' Raised when a metadata document is structurally invalid or not the expected
' document type; the caller maps this to the InvalidMetadata exit code.
Public Class MetadataInvalidException
    Inherits Exception

    Public Sub New(message As String)
        MyBase.New(message)
    End Sub
End Class

' Parsed ADFS style federation metadata (SAML 2.0 EntityDescriptor).
Public NotInheritable Class AdfsMetadata
    Public Sub New(entityId As String,
                   singleSignOnEndpoint As String,
                   binding As String,
                   certificateThumbprint As String,
                   certificateSubject As String,
                   certificateNotAfter As DateTimeOffset)
        Me.EntityId = entityId
        Me.SingleSignOnEndpoint = singleSignOnEndpoint
        Me.Binding = binding
        Me.CertificateThumbprint = certificateThumbprint
        Me.CertificateSubject = certificateSubject
        Me.CertificateNotAfter = certificateNotAfter
    End Sub

    Public ReadOnly Property EntityId As String
    Public ReadOnly Property SingleSignOnEndpoint As String
    Public ReadOnly Property Binding As String
    Public ReadOnly Property CertificateThumbprint As String
    Public ReadOnly Property CertificateSubject As String
    Public ReadOnly Property CertificateNotAfter As DateTimeOffset
End Class

' Parsed OIDC discovery document fields the tool reports.
Public NotInheritable Class DiscoveryMetadata
    Public Sub New(issuer As String,
                   authorizationEndpoint As String,
                   tokenEndpoint As String,
                   jwksUri As String)
        Me.Issuer = issuer
        Me.AuthorizationEndpoint = authorizationEndpoint
        Me.TokenEndpoint = tokenEndpoint
        Me.JwksUri = jwksUri
    End Sub

    Public ReadOnly Property Issuer As String
    Public ReadOnly Property AuthorizationEndpoint As String
    Public ReadOnly Property TokenEndpoint As String
    Public ReadOnly Property JwksUri As String
End Class

' One RSA key entry of a JWKS document.
Public NotInheritable Class JwkKeyRecord
    Public Sub New(kid As String,
                   keyType As String,
                   use As String,
                   algorithm As String,
                   modulus As String,
                   exponent As String)
        Me.Kid = kid
        Me.KeyType = keyType
        Me.Use = use
        Me.Algorithm = algorithm
        Me.Modulus = modulus
        Me.Exponent = exponent
    End Sub

    Public ReadOnly Property Kid As String
    Public ReadOnly Property KeyType As String
    Public ReadOnly Property Use As String
    Public ReadOnly Property Algorithm As String
    Public ReadOnly Property Modulus As String
    Public ReadOnly Property Exponent As String
End Class

' Pure parsing of federation metadata: ADFS EntityDescriptor XML (with DTD
' prohibited, per the security findings log) and OIDC discovery JSON. No HTTP
' lives here, so the tests feed strings.
Public Module MetadataParser

    Public Const AdfsMetadataPath As String = "/federationmetadata/2007-06/federationmetadata.xml"
    Public Const OktaDiscoveryPath As String = "/.well-known/openid-configuration"

    ' Parses an EntityDescriptor: entity id, SSO endpoint with binding, and
    ' the signing certificate thumbprint. Throws MetadataInvalidException for
    ' anything that is not a usable document.
    Public Function ParseAdfsMetadata(xml As String) As AdfsMetadata
        If String.IsNullOrWhiteSpace(xml) Then
            Throw New MetadataInvalidException("metadata document is empty")
        End If

        ' Safe XML: DTD prohibited, no resolver, so no entity expansion and
        ' no external fetches while parsing.
        Dim settings = New XmlReaderSettings()
        settings.DtdProcessing = DtdProcessing.Prohibit
        settings.XmlResolver = Nothing

        Dim root As XElement
        Try
            Using reader = XmlReader.Create(New StringReader(xml), settings)
                root = XElement.Load(reader)
            End Using
        Catch ex As XmlException
            Throw New MetadataInvalidException("metadata XML is not well formed: " & ex.Message)
        End Try

        Return ReadAdfsDocument(root)
    End Function

    ' Parses an OIDC discovery document; issuer and jwks_uri are required,
    ' the endpoint entries are reported when present.
    Public Function ParseDiscovery(json As String) As DiscoveryMetadata
        Try
            Using document = JsonDocument.Parse(json)
                Dim rootElement = document.RootElement
                If rootElement.ValueKind <> JsonValueKind.Object Then
                    Throw New MetadataInvalidException("discovery document is not a JSON object")
                End If
                Return New DiscoveryMetadata(RequiredDiscoveryString(rootElement, "issuer"),
                                             OptionalDiscoveryString(rootElement, "authorization_endpoint"),
                                             OptionalDiscoveryString(rootElement, "token_endpoint"),
                                             RequiredDiscoveryString(rootElement, "jwks_uri"))
            End Using
        Catch ex As JsonException
            Throw New MetadataInvalidException("discovery document is not valid JSON: " & ex.Message)
        End Try
    End Function

    ' All RSA key records listed in a JWKS document.
    Public Function ParseJwksKeys(jwksJson As String) As IReadOnlyList(Of JwkKeyRecord)
        Dim keys = New List(Of JwkKeyRecord)
        Using document = JsonDocument.Parse(jwksJson)
            Dim rootElement = document.RootElement
            If rootElement.ValueKind = JsonValueKind.Object Then
                Dim keysElement As JsonElement
                If rootElement.TryGetProperty("keys", keysElement) AndAlso keysElement.ValueKind = JsonValueKind.Array Then
                    For Each keyElement As JsonElement In keysElement.EnumerateArray()
                        keys.Add(New JwkKeyRecord(ReadJsonText(keyElement, "kid"),
                                                  ReadJsonText(keyElement, "kty"),
                                                  ReadJsonText(keyElement, "use"),
                                                  ReadJsonText(keyElement, "alg"),
                                                  ReadJsonText(keyElement, "n"),
                                                  ReadJsonText(keyElement, "e")))
                    Next
                End If
            End If
        End Using
        Return keys
    End Function

    ' Key ids in a JWKS document, for the check-metadata report.
    Public Function ParseJwksKids(jwksJson As String) As IReadOnlyList(Of String)
        Dim kids = New List(Of String)
        For Each keyRecord In ParseJwksKeys(jwksJson)
            If Not String.IsNullOrEmpty(keyRecord.Kid) Then
                kids.Add(keyRecord.Kid)
            End If
        Next
        Return kids
    End Function

    Private Function ReadAdfsDocument(root As XElement) As AdfsMetadata
        If Not String.Equals(root.Name.LocalName, "EntityDescriptor", StringComparison.Ordinal) Then
            Throw New MetadataInvalidException("root element is " & root.Name.LocalName & ", expected EntityDescriptor")
        End If

        Dim entityId = AttributeValue(root, "entityID")
        If String.IsNullOrEmpty(entityId) Then
            Throw New MetadataInvalidException("EntityDescriptor carries no entityID attribute")
        End If

        Dim ssoElement = root.Descendants().
            FirstOrDefault(Function(e) String.Equals(e.Name.LocalName, "SingleSignOnService", StringComparison.Ordinal))
        If ssoElement Is Nothing Then
            Throw New MetadataInvalidException("metadata has no SingleSignOnService endpoint")
        End If
        Dim binding = AttributeValue(ssoElement, "Binding")
        Dim location = AttributeValue(ssoElement, "Location")
        If String.IsNullOrEmpty(location) Then
            Throw New MetadataInvalidException("SingleSignOnService carries no Location")
        End If

        Dim certificateElement = root.Descendants().
            FirstOrDefault(Function(e) String.Equals(e.Name.LocalName, "X509Certificate", StringComparison.Ordinal))
        Dim certificateText = If(certificateElement Is Nothing, Nothing, certificateElement.Value.Trim())
        If String.IsNullOrEmpty(certificateText) Then
            Throw New MetadataInvalidException("metadata has no X509Certificate signing key")
        End If

        Dim thumbprint As String = ""
        Dim subject As String = ""
        Dim notAfter As DateTimeOffset = DateTimeOffset.MinValue
        Try
            Using certificate = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(certificateText))
                thumbprint = certificate.Thumbprint
                subject = certificate.Subject
                notAfter = New DateTimeOffset(certificate.NotAfter)
            End Using
        Catch ex As FormatException
            Throw New MetadataInvalidException("signing certificate is not valid base64: " & ex.Message)
        Catch ex As ArgumentException
            Throw New MetadataInvalidException("signing certificate could not be parsed: " & ex.Message)
        Catch ex As CryptographicException
            Throw New MetadataInvalidException("signing certificate could not be parsed: " & ex.Message)
        End Try

        Return New AdfsMetadata(entityId, location, binding, thumbprint, subject, notAfter)
    End Function

    Private Function AttributeValue(element As XElement, name As String) As String
        Dim attribute = element.Attribute(name)
        If attribute Is Nothing Then
            Return Nothing
        End If
        Return attribute.Value
    End Function

    Private Function RequiredDiscoveryString(rootElement As JsonElement, propertyName As String) As String
        Dim found As JsonElement
        If Not rootElement.TryGetProperty(propertyName, found) OrElse found.ValueKind <> JsonValueKind.String Then
            Throw New MetadataInvalidException("discovery document has no " & propertyName & " string")
        End If
        Return found.GetString()
    End Function

    Private Function OptionalDiscoveryString(rootElement As JsonElement, propertyName As String) As String
        Dim found As JsonElement
        If rootElement.TryGetProperty(propertyName, found) AndAlso found.ValueKind = JsonValueKind.String Then
            Return found.GetString()
        End If
        Return Nothing
    End Function

    Private Function ReadJsonText(element As JsonElement, propertyName As String) As String
        Dim found As JsonElement
        If Not element.TryGetProperty(propertyName, found) Then
            Return Nothing
        End If
        If found.ValueKind = JsonValueKind.String Then
            Return found.GetString()
        End If
        Return found.GetRawText()
    End Function
End Module
