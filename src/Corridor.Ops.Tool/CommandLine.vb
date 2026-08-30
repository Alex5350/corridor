Imports System
Imports System.Collections.Generic

' Minimal argument parsing for the sub command style: bare words become
' positional arguments, while --name value and --name=value become options.
' The single exception is -h, which is kept as an option so every command can
' answer it the same way.
Public NotInheritable Class ParsedArguments

    Private ReadOnly _positional As IReadOnlyList(Of String)
    Private ReadOnly _options As IReadOnlyDictionary(Of String, String)

    Public Sub New(positional As IReadOnlyList(Of String), options As IReadOnlyDictionary(Of String, String))
        _positional = positional
        _options = options
    End Sub

    Public ReadOnly Property Positional As IReadOnlyList(Of String)
        Get
            Return _positional
        End Get
    End Property

    ' The value of an option, or Nothing when the caller did not supply it.
    Public Function GetOption(name As String) As String
        Dim value As String = Nothing
        If _options.TryGetValue(name, value) Then
            Return value
        End If
        Return Nothing
    End Function

    ' The value of an option, or the given fallback.
    Public Function GetOption(name As String, fallback As String) As String
        Dim value = GetOption(name)
        If value Is Nothing Then
            Return fallback
        End If
        Return value
    End Function

    Public Function HasOption(name As String) As Boolean
        Return _options.ContainsKey(name)
    End Function
End Class

Public Module CommandLine

    Public Function Parse(args As IEnumerable(Of String)) As ParsedArguments
        Dim positional = New List(Of String)
        Dim options = New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        Dim tokens = New Queue(Of String)(args)

        While tokens.Count > 0
            Dim token = tokens.Dequeue()

            If token = "-h" Then
                options("h") = String.Empty
                Continue While
            End If

            If Not token.StartsWith("--", StringComparison.Ordinal) Then
                positional.Add(token)
                Continue While
            End If

            Dim name = token.Substring(2)
            Dim value As String
            Dim separator = name.IndexOf("="c)
            If separator >= 0 Then
                value = name.Substring(separator + 1)
                name = name.Substring(0, separator)
            ElseIf tokens.Count > 0 Then
                value = tokens.Dequeue()
            Else
                value = String.Empty
            End If

            If name.Length = 0 Then
                Throw New ArgumentException("option without a name: " & token)
            End If
            options(name) = value
        End While

        Return New ParsedArguments(positional, options)
    End Function
End Module
