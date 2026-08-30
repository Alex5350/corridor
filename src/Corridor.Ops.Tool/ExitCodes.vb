Imports System.Net.Http
Imports System.Text.Json
Imports System.Xml

' Named process exit codes. USAGE.md mirrors this table; the tests pin the
' numbers so the runbook never drifts from the binary.
Public Module ExitCodes

    ' Command completed successfully.
    Public Const Success As Integer = 0

    ' Bad invocation: unknown command, missing or invalid arguments.
    Public Const Usage As Integer = 1

    ' Metadata was fetched but is malformed or not the expected document.
    Public Const InvalidMetadata As Integer = 2

    ' Endpoint could not be reached, timed out, or failed at the transport layer.
    Public Const Unreachable As Integer = 3

    ' Token failed validation (structure, signature, iss, aud, exp) or is malformed.
    Public Const InvalidToken As Integer = 4

    ' SCIM endpoint error: non-success status, transport failure, or bad payload.
    Public Const ScimError As Integer = 5

    ' Maps a metadata fetch or parse failure onto the documented codes:
    ' structural problems are invalid metadata (2), transport problems are
    ' unreachable (3), anything else is a usage level failure (1).
    Public Function ForMetadataFailure(failure As Exception) As Integer
        If TypeOf failure Is MetadataInvalidException OrElse
           TypeOf failure Is XmlException OrElse
           TypeOf failure Is JsonException Then
            Return InvalidMetadata
        End If
        If TypeOf failure Is HttpRequestException OrElse
           TypeOf failure Is TaskCanceledException OrElse
           TypeOf failure Is TimeoutException Then
            Return Unreachable
        End If
        Return Usage
    End Function
End Module
