Imports System
Imports System.Net.Http

' One HttpClient for the whole process with the 5 second timeout the runbook
' promises. Commands add their own headers; nothing here carries credentials.
Public Module SharedHttp

    Private ReadOnly ClientField As HttpClient

    Sub New()
        ClientField = New HttpClient()
        ClientField.Timeout = TimeSpan.FromSeconds(5)
        ClientField.DefaultRequestHeaders.UserAgent.ParseAdd("corridor-ops/1.0")
    End Sub

    Public ReadOnly Property Client As HttpClient
        Get
            Return ClientField
        End Get
    End Property
End Module
