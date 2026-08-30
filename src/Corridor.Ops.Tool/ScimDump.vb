Imports System
Imports System.Collections.Generic
Imports System.Net.Http
Imports System.Net.Http.Headers
Imports System.Text.Json
Imports System.Threading.Tasks

' Transport or non-success response from the SCIM endpoint. Messages never
' include the bearer token.
Public Class ScimRequestException
    Inherits Exception

    Public Sub New(message As String)
        MyBase.New(message)
    End Sub
End Class

' SCIM 2.0 user listing: URL building, parsing, and rendering. The HTTP hop
' takes an injected HttpClient so tests can fake the transport.
Public Module ScimDump

    Public Const UsersPath As String = "/scim/v2/Users"

    ' One user row: userName, active, externalId.
    Public NotInheritable Class ScimUser
        Public Sub New(userName As String, active As Boolean?, externalId As String)
            Me.UserName = userName
            Me.Active = active
            Me.ExternalId = externalId
        End Sub

        Public ReadOnly Property UserName As String
        Public ReadOnly Property Active As Boolean?
        Public ReadOnly Property ExternalId As String
    End Class

    ' Builds the users URL from a base url; a caller who already pasted the
    ' full endpoint is honored as is.
    Public Function BuildUrl(baseUrl As String) As String
        Dim trimmed = baseUrl.TrimEnd("/"c)
        If trimmed.EndsWith(UsersPath, StringComparison.OrdinalIgnoreCase) Then
            Return trimmed
        End If
        Return trimmed & UsersPath
    End Function

    ' Maps a SCIM list response (or a bare array) onto user records.
    Public Function ParseUsers(json As String) As IReadOnlyList(Of ScimUser)
        Dim users = New List(Of ScimUser)
        Using document = JsonDocument.Parse(json)
            Dim rootElement = document.RootElement
            If rootElement.ValueKind = JsonValueKind.Array Then
                AppendUsers(rootElement, users)
            ElseIf rootElement.ValueKind = JsonValueKind.Object Then
                Dim resourcesElement As JsonElement
                If rootElement.TryGetProperty("Resources", resourcesElement) AndAlso
                   resourcesElement.ValueKind = JsonValueKind.Array Then
                    AppendUsers(resourcesElement, users)
                End If
            End If
        End Using
        Return users
    End Function

    ' Renders the operator table: userName, active, externalId, with long
    ' values truncated by TextTable so the layout survives odd data.
    Public Function RenderTable(users As IReadOnlyList(Of ScimUser)) As String
        Dim table = New TextTable({"userName", "active", "externalId"}, {28, 8, 40})
        For Each user As ScimUser In users
            table.AddRow(If(user.UserName, ""), RenderActive(user.Active), If(user.ExternalId, ""))
        Next
        Return table.Render()
    End Function

    ' GETs the users endpoint with the bearer token and returns the raw body;
    ' non-success statuses raise ScimRequestException.
    Public Async Function FetchAsync(client As HttpClient,
                                     fullUrl As String,
                                     bearerToken As String) As Task(Of String)
        Using request = New HttpRequestMessage(HttpMethod.Get, fullUrl)
            request.Headers.Authorization = New AuthenticationHeaderValue("Bearer", bearerToken)
            Using response = Await client.SendAsync(request)
                Dim body = Await response.Content.ReadAsStringAsync()
                If Not response.IsSuccessStatusCode Then
                    Throw New ScimRequestException("SCIM endpoint returned HTTP " &
                                                   CInt(response.StatusCode) &
                                                   " " & If(response.ReasonPhrase, ""))
                End If
                Return body
            End Using
        End Using
    End Function

    Private Sub AppendUsers(arrayElement As JsonElement, users As List(Of ScimUser))
        For Each userElement As JsonElement In arrayElement.EnumerateArray()
            Dim active As Boolean? = Nothing
            Dim activeElement As JsonElement
            If userElement.TryGetProperty("active", activeElement) Then
                If activeElement.ValueKind = JsonValueKind.True Then
                    active = True
                ElseIf activeElement.ValueKind = JsonValueKind.False Then
                    active = False
                End If
            End If
            users.Add(New ScimUser(ReadUserString(userElement, "userName"),
                                   active,
                                   ReadUserString(userElement, "externalId")))
        Next
    End Sub

    Private Function ReadUserString(userElement As JsonElement, propertyName As String) As String
        Dim found As JsonElement
        If userElement.TryGetProperty(propertyName, found) AndAlso found.ValueKind = JsonValueKind.String Then
            Return found.GetString()
        End If
        Return ""
    End Function

    Private Function RenderActive(active As Boolean?) As String
        If active Is Nothing Then
            Return ""
        End If
        Return If(active.Value, "true", "false")
    End Function
End Module
