Imports System

' Console output helpers: plain ANSI coloring that switches itself off when the
' NO_COLOR environment variable is set to any non-empty value. Tests flip the
' variable and check Colorize, which is why the check happens per call.
Public Module Output

    Private ReadOnly EscapeCharacter As Char = Convert.ToChar(27)

    ' Console colors the tool uses; Plain means no escape sequences at all.
    Public Enum AnsiColor
        Plain
        Red
        Green
        Yellow
        Cyan
        Bold
    End Enum

    ' True when the caller asked for no color via the NO_COLOR convention.
    Public ReadOnly Property NoColorRequested As Boolean
        Get
            Return Not String.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"))
        End Get
    End Property

    ' Wraps text in the ANSI sequence for the color, or returns it untouched
    ' when NO_COLOR is set or the color is Plain.
    Public Function Colorize(text As String, color As AnsiColor) As String
        If color = AnsiColor.Plain OrElse NoColorRequested Then
            Return text
        End If
        Return SequenceFor(color) & text & SequenceFor(AnsiColor.Plain)
    End Function

    Public Sub WriteLine(text As String)
        Console.WriteLine(text)
    End Sub

    Public Sub WriteLine(text As String, color As AnsiColor)
        Console.WriteLine(Colorize(text, color))
    End Sub

    Public Sub Info(text As String)
        WriteLine(text)
    End Sub

    Public Sub Warn(text As String)
        WriteLine("warning: " & text, AnsiColor.Yellow)
    End Sub

    Public Sub Fail(text As String)
        WriteLine("error: " & text, AnsiColor.Red)
    End Sub

    Public Sub Pass(text As String)
        WriteLine(text, AnsiColor.Green)
    End Sub

    Private Function SequenceFor(color As AnsiColor) As String
        Select Case color
            Case AnsiColor.Red
                Return EscapeCharacter & "[31m"
            Case AnsiColor.Green
                Return EscapeCharacter & "[32m"
            Case AnsiColor.Yellow
                Return EscapeCharacter & "[33m"
            Case AnsiColor.Cyan
                Return EscapeCharacter & "[36m"
            Case AnsiColor.Bold
                Return EscapeCharacter & "[1m"
            Case Else
                Return EscapeCharacter & "[0m"
        End Select
    End Function
End Module
