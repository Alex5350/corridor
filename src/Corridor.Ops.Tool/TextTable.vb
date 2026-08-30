Imports System
Imports System.Collections.Generic
Imports System.Text

' Fixed width text table for console output. Cells stay single line: newlines
' collapse to spaces and over long cells are cut with an ellipsis, so a hostile
' claim value can never break the layout.
Public NotInheritable Class TextTable

    Private ReadOnly _headers As String()
    Private ReadOnly _maxWidths As Integer()
    Private ReadOnly _rows As New List(Of String())

    Public Sub New(headers As String(), maxWidths As Integer())
        If headers Is Nothing Then
            Throw New ArgumentNullException(NameOf(headers))
        End If
        If maxWidths Is Nothing Then
            Throw New ArgumentNullException(NameOf(maxWidths))
        End If
        If headers.Length <> maxWidths.Length Then
            Throw New ArgumentException("headers and maxWidths must have the same length")
        End If
        _headers = headers
        _maxWidths = maxWidths
    End Sub

    Public Sub AddRow(ParamArray cells As String())
        Dim copy(cells.Length - 1) As String
        For index = 0 To cells.Length - 1
            copy(index) = If(cells(index) Is Nothing, "", cells(index))
        Next
        _rows.Add(copy)
    End Sub

    ' Header row, a dash separator, then one line per row.
    Public Function Render() As String
        Dim widths = ComputeWidths()
        Dim builder = New StringBuilder()
        builder.AppendLine(FormatRow(_headers, widths))
        builder.AppendLine(New String("-"c, SeparatorLength(widths)))
        For Each row As String() In _rows
            builder.AppendLine(FormatRow(row, widths))
        Next
        Return builder.ToString().TrimEnd()
    End Function

    Private Function ComputeWidths() As Integer()
        Dim widths(_headers.Length - 1) As Integer
        For column = 0 To _headers.Length - 1
            Dim headerWidth = SingleLine(_headers(column)).Length
            Dim longest = headerWidth
            For Each row As String() In _rows
                If column < row.Length Then
                    longest = Math.Max(longest, SingleLine(row(column)).Length)
                End If
            Next
            ' Long cells get truncated to the max; the header keeps full width.
            widths(column) = Math.Max(headerWidth, Math.Min(longest, _maxWidths(column)))
        Next
        Return widths
    End Function

    Private Function FormatRow(cells As String(), widths As Integer()) As String
        Dim builder = New StringBuilder()
        For column = 0 To widths.Length - 1
            Dim cell = If(column < cells.Length, cells(column), "")
            builder.Append(Fit(SingleLine(cell), widths(column)).PadRight(widths(column)))
            If column < widths.Length - 1 Then
                builder.Append("  ")
            End If
        Next
        Return builder.ToString().TrimEnd()
    End Function

    Private Function SeparatorLength(widths As Integer()) As Integer
        Dim total As Integer = 0
        For Each width In widths
            total += width
        Next
        If widths.Length > 1 Then
            total += (widths.Length - 1) * 2
        End If
        Return total
    End Function

    Private Shared Function SingleLine(text As String) As String
        If String.IsNullOrEmpty(text) Then
            Return ""
        End If
        Return text.Replace(vbCrLf, " ").Replace(vbLf, " ").Replace(vbCr, " ")
    End Function

    Private Shared Function Fit(text As String, width As Integer) As String
        If text.Length <= width Then
            Return text
        End If
        If width <= 3 Then
            Return text.Substring(0, width)
        End If
        Return text.Substring(0, width - 3) & "..."
    End Function
End Class
