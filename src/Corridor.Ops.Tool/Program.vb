Imports System
Imports System.Linq

' Entry point: the first argument selects the sub command, the rest go to the
' matching command function, and its return value becomes the exit code.
Public Module Program

    Public Function Main(args As String()) As Integer
        If args Is Nothing OrElse args.Length = 0 Then
            Output.WriteLine(HelpText.GeneralHelp())
            Return ExitCodes.Usage
        End If

        Dim command = args(0)
        Dim rest = args.Skip(1).ToArray()

        Select Case command.ToLowerInvariant()
            Case "check-metadata"
                Return Commands.RunCheckMetadata(rest)
            Case "decode-token"
                Return Commands.RunDecodeToken(rest)
            Case "validate-token"
                Return Commands.RunValidateToken(rest)
            Case "scim-dump"
                Return Commands.RunScimDump(rest)
            Case "whoami-token"
                Return Commands.RunWhoAmI(rest)
            Case "--help", "-h", "help"
                Output.WriteLine(HelpText.GeneralHelp())
                Return ExitCodes.Success
            Case Else
                Output.Fail("unknown command: " & command)
                Output.WriteLine(HelpText.GeneralHelp())
                Return ExitCodes.Usage
        End Select
    End Function
End Module
