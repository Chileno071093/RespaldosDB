Imports System
Imports System.Collections.Generic
Imports System.Text
Imports System.Text.RegularExpressions
Imports Microsoft.VisualBasic

Friend NotInheritable Class DeclaredObject
    Public Property Kind As String
    Public Property DatabaseName As String
    Public Property SchemaName As String
    Public Property ObjectName As String
    Public Property SourceFile As String
    Public Property CandidateText As String

    Public Function DisplayName() As String
        Dim prefix As String = If(String.IsNullOrEmpty(SchemaName), "", "[" & SchemaName & "].")
        Return Kind & " " & prefix & "[" & ObjectName & "]"
    End Function
End Class

Friend NotInheritable Class SqlObjectParser
    Private Enum TokenKind
        Word
        Identifier
        Dot
        Other
    End Enum

    Private Structure SqlToken
        Public ReadOnly Kind As TokenKind
        Public ReadOnly Value As String
        Public ReadOnly Start As Integer

        Public Sub New(kind As TokenKind, value As String, start As Integer)
            Me.Kind = kind
            Me.Value = value
            Me.Start = start
        End Sub
    End Structure

    Private Sub New()
    End Sub

    Public Shared Function Parse(script As String, sourceFile As String) As List(Of DeclaredObject)
        Dim tokens As List(Of SqlToken) = Tokenize(script)
        Dim found As New List(Of DeclaredObject)()
        For index As Integer = 0 To tokens.Count - 1
            If Not IsWord(tokens(index), "CREATE") AndAlso Not IsWord(tokens(index), "ALTER") Then
                Continue For
            End If

            Dim position As Integer = index + 1
            If IsWord(tokens(index), "CREATE") AndAlso
               position + 1 < tokens.Count AndAlso
               IsWord(tokens(position), "OR") AndAlso
               IsWord(tokens(position + 1), "ALTER") Then
                position += 2
            End If
            If position >= tokens.Count Then
                Continue For
            End If

            Dim kind As String = ObjectKind(tokens(position))
            If kind Is Nothing Then
                Continue For
            End If
            position += 1
            If position >= tokens.Count OrElse Not IsIdentifier(tokens(position)) Then
                Continue For
            End If

            Dim parts As New List(Of String) From {tokens(position).Value}
            While position + 2 < tokens.Count AndAlso
                  tokens(position + 1).Kind = TokenKind.Dot AndAlso
                  IsIdentifier(tokens(position + 2)) AndAlso
                  parts.Count < 3
                position += 2
                parts.Add(tokens(position).Value)
            End While

            Dim item As New DeclaredObject With {.Kind = kind, .SourceFile = sourceFile}
            Dim batchEnd As Integer = FindBatchEnd(script, tokens, position + 1)
            item.CandidateText = script.Substring(tokens(index).Start, batchEnd - tokens(index).Start).Trim()
            Select Case parts.Count
                Case 1
                    item.ObjectName = parts(0)
                Case 2
                    item.SchemaName = parts(0)
                    item.ObjectName = parts(1)
                Case 3
                    item.DatabaseName = parts(0)
                    item.SchemaName = parts(1)
                    item.ObjectName = parts(2)
            End Select
            found.Add(item)
        Next
        Return found
    End Function

    Private Shared Function FindBatchEnd(script As String, tokens As List(Of SqlToken), fromIndex As Integer) As Integer
        For index As Integer = fromIndex To tokens.Count - 1
            If Not IsWord(tokens(index), "GO") Then Continue For
            Dim lineStart As Integer = script.LastIndexOfAny({ChrW(10), ChrW(13)}, Math.Max(0, tokens(index).Start - 1)) + 1
            Dim lineEnd As Integer = script.IndexOfAny({ChrW(10), ChrW(13)}, tokens(index).Start)
            If lineEnd < 0 Then lineEnd = script.Length
            Dim line As String = script.Substring(lineStart, lineEnd - lineStart)
            If Regex.IsMatch(line, "^\s*GO(?:\s+\d+)?\s*(?:--.*)?$", RegexOptions.IgnoreCase) Then
                Return lineStart
            End If
        Next
        Return script.Length
    End Function

    Private Shared Function IsWord(token As SqlToken, value As String) As Boolean
        Return token.Kind = TokenKind.Word AndAlso String.Equals(token.Value, value, StringComparison.OrdinalIgnoreCase)
    End Function

    Private Shared Function ObjectKind(token As SqlToken) As String
        If IsWord(token, "PROC") OrElse IsWord(token, "PROCEDURE") Then Return "Procedimiento"
        If IsWord(token, "VIEW") Then Return "Vista"
        If IsWord(token, "TRIGGER") Then Return "Trigger"
        If IsWord(token, "FUNCTION") Then Return "Funcion"
        Return Nothing
    End Function

    Private Shared Function IsIdentifier(token As SqlToken) As Boolean
        Return token.Kind = TokenKind.Word OrElse token.Kind = TokenKind.Identifier
    End Function

    Private Shared Function IsWordCharacter(value As Char) As Boolean
        Return Char.IsLetterOrDigit(value) OrElse value = "_"c OrElse value = "@"c OrElse value = "#"c OrElse value = "$"c
    End Function

    Private Shared Function Tokenize(script As String) As List(Of SqlToken)
        Dim tokens As New List(Of SqlToken)()
        Dim index As Integer = 0
        While index < script.Length
            Dim tokenStart As Integer = index
            Dim current As Char = script(index)
            Dim nextChar As Char = If(index + 1 < script.Length, script(Math.Min(index + 1, script.Length - 1)), ChrW(0))

            If Char.IsWhiteSpace(current) Then
                index += 1
            ElseIf current = "-"c AndAlso nextChar = "-"c Then
                index += 2
                While index < script.Length AndAlso script(index) <> ChrW(10)
                    index += 1
                End While
            ElseIf current = "/"c AndAlso nextChar = "*"c Then
                index += 2
                Dim depth As Integer = 1
                While index < script.Length AndAlso depth > 0
                    Dim following As Char = If(index + 1 < script.Length, script(Math.Min(index + 1, script.Length - 1)), ChrW(0))
                    If script(index) = "/"c AndAlso following = "*"c Then
                        depth += 1
                        index += 2
                    ElseIf script(index) = "*"c AndAlso following = "/"c Then
                        depth -= 1
                        index += 2
                    Else
                        index += 1
                    End If
                End While
            ElseIf current = "'"c Then
                index += 1
                While index < script.Length
                    If script(index) = "'"c Then
                        If index + 1 < script.Length AndAlso script(index + 1) = "'"c Then
                            index += 2
                        Else
                            index += 1
                            Exit While
                        End If
                    Else
                        index += 1
                    End If
                End While
            ElseIf current = "["c Then
                index += 1
                Dim value As New StringBuilder()
                While index < script.Length
                    If script(index) = "]"c Then
                        If index + 1 < script.Length AndAlso script(index + 1) = "]"c Then
                            value.Append("]"c)
                            index += 2
                        Else
                            index += 1
                            Exit While
                        End If
                    Else
                        value.Append(script(index))
                        index += 1
                    End If
                End While
                tokens.Add(New SqlToken(TokenKind.Identifier, value.ToString(), tokenStart))
            ElseIf current = """"c Then
                index += 1
                Dim value As New StringBuilder()
                While index < script.Length
                    If script(index) = """"c Then
                        If index + 1 < script.Length AndAlso script(index + 1) = """"c Then
                            value.Append(""""c)
                            index += 2
                        Else
                            index += 1
                            Exit While
                        End If
                    Else
                        value.Append(script(index))
                        index += 1
                    End If
                End While
                tokens.Add(New SqlToken(TokenKind.Identifier, value.ToString(), tokenStart))
            ElseIf IsWordCharacter(current) Then
                Dim start As Integer = index
                Do
                    index += 1
                Loop While index < script.Length AndAlso IsWordCharacter(script(index))
                tokens.Add(New SqlToken(TokenKind.Word, script.Substring(start, index - start), tokenStart))
            ElseIf current = "."c Then
                tokens.Add(New SqlToken(TokenKind.Dot, ".", tokenStart))
                index += 1
            Else
                tokens.Add(New SqlToken(TokenKind.Other, current.ToString(), tokenStart))
                index += 1
            End If
        End While
        Return tokens
    End Function
End Class
