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
    ' Posicion del CREATE/ALTER (o DROP) dentro del script (lo previo suelen ser comentarios).
    Public Property DeclarationStart As Integer
    ' La sentencia es un DROP: la liberacion elimina el objeto.
    Public Property IsDrop As Boolean
    ' Trigger de servidor (ON ALL SERVER): no pertenece a ninguna base.
    Public Property IsServerScoped As Boolean
    ' El script tambien lo elimina con DROP antes de crearlo: se pierden sus permisos.
    Public Property AlsoDropped As Boolean

    Public Function DisplayName() As String
        Dim prefix As String = If(String.IsNullOrEmpty(SchemaName), "", "[" & SchemaName & "].")
        Return Kind & " " & prefix & "[" & ObjectName & "]"
    End Function
End Class

' Sentencia de la liberacion que el respaldo y el script de reversion no cubren (tablas, datos, permisos...).
Friend NotInheritable Class UnsupportedStatement
    Public Property SourceFile As String
    Public Property Line As Integer
    Public Property Category As String
    Public Property Text As String

    Public Overrides Function ToString() As String
        Return SourceFile & ":" & Line.ToString() & " | " & Category & " | " & Text
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

    ' Declaraciones CREATE / ALTER / CREATE OR ALTER y DROP de los objetos soportados.
    Public Shared Function Parse(script As String, sourceFile As String) As List(Of DeclaredObject)
        Return Scan(script, sourceFile, Nothing)
    End Function

    ' Igual que Parse y, en la misma pasada, agrega a "unsupported" las sentencias que el script de reversion
    ' no puede deshacer (tablas, datos, permisos...). Lo que esta dentro del cuerpo de un objeto no cuenta.
    Public Shared Function Parse(script As String, sourceFile As String, unsupported As List(Of UnsupportedStatement)) As List(Of DeclaredObject)
        Return Scan(script, sourceFile, unsupported)
    End Function

    Private Shared ReadOnly StructureWords As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase) From {
        "TABLE", "INDEX", "UNIQUE", "CLUSTERED", "NONCLUSTERED", "COLUMNSTORE", "STATISTICS", "TYPE", "SEQUENCE",
        "SCHEMA", "DEFAULT", "RULE", "XML", "FULLTEXT", "PARTITION", "SPATIAL"}
    Private Shared ReadOnly SecurityWords As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase) From {
        "USER", "ROLE", "LOGIN", "APPLICATION", "CERTIFICATE", "ASYMMETRIC", "SYMMETRIC", "MASTER", "CREDENTIAL"}
    Private Shared ReadOnly DataWords As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase) From {
        "INSERT", "UPDATE", "DELETE", "MERGE", "TRUNCATE"}

    Private Shared Function Scan(script As String, sourceFile As String, unsupported As List(Of UnsupportedStatement)) As List(Of DeclaredObject)
        Dim tokens As List(Of SqlToken) = Tokenize(script)
        Dim found As New List(Of DeclaredObject)()
        ' Fin del cuerpo de la ultima declaracion: lo que hay dentro (INSERT, DROP TABLE #t...) es parte del objeto.
        Dim bodyEnd As Integer = -1
        Dim lastReportedLine As Integer = 0
        Dim report As Action(Of Integer, String) =
            Sub(tokenIndex As Integer, category As String)
                If unsupported Is Nothing Then Return
                Dim line As Integer = LineNumber(script, tokens(tokenIndex).Start)
                If line = lastReportedLine Then Return
                lastReportedLine = line
                unsupported.Add(New UnsupportedStatement With {.SourceFile = sourceFile, .Line = line, .Category = category, .Text = LineText(script, tokens(tokenIndex).Start)})
            End Sub

        For index As Integer = 0 To tokens.Count - 1
            Dim token As SqlToken = tokens(index)
            If token.Start < bodyEnd Then Continue For

            If IsWord(token, "CREATE") OrElse IsWord(token, "ALTER") Then
                Dim position As Integer = index + 1
                If IsWord(token, "CREATE") AndAlso position + 1 < tokens.Count AndAlso
                   IsWord(tokens(position), "OR") AndAlso IsWord(tokens(position + 1), "ALTER") Then
                    position += 2
                End If
                If position >= tokens.Count Then Continue For
                Dim kind As String = ObjectKind(tokens(position))
                If kind Is Nothing Then
                    ReportOtherObject(tokens, position, report)
                    Continue For
                End If
                Dim parts As List(Of String) = ReadName(tokens, position + 1, position)
                If parts Is Nothing Then Continue For

                Dim item As DeclaredObject = NewDeclared(kind, parts, sourceFile, token.Start)
                item.IsServerScoped = kind = "Trigger" AndAlso IsOnAllServer(tokens, position + 1)
                Dim batchEnd As Integer = FindBatchEnd(script, tokens, position + 1)
                item.CandidateText = script.Substring(token.Start, batchEnd - token.Start).Trim()
                found.Add(item)
                bodyEnd = batchEnd
            ElseIf IsWord(token, "DROP") Then
                If index + 1 >= tokens.Count Then Continue For
                Dim kind As String = ObjectKind(tokens(index + 1))
                If kind Is Nothing Then
                    ReportOtherObject(tokens, index + 1, report)
                    Continue For
                End If
                Dim position As Integer = index + 2
                If position + 1 < tokens.Count AndAlso IsWord(tokens(position), "IF") AndAlso IsWord(tokens(position + 1), "EXISTS") Then position += 2
                Dim dropped As New List(Of DeclaredObject)()
                Do
                    Dim lastToken As Integer
                    Dim parts As List(Of String) = ReadName(tokens, position, lastToken)
                    If parts Is Nothing Then Exit Do
                    dropped.Add(NewDeclared(kind, parts, sourceFile, token.Start))
                    position = lastToken + 1
                    If position < tokens.Count AndAlso tokens(position).Kind = TokenKind.Other AndAlso tokens(position).Value = "," Then
                        position += 1
                    Else
                        Exit Do
                    End If
                Loop
                Dim serverScoped As Boolean = kind = "Trigger" AndAlso IsOnAllServer(tokens, position)
                Dim statementText As String = LineText(script, token.Start)
                For Each item As DeclaredObject In dropped
                    item.IsDrop = True
                    item.IsServerScoped = serverScoped
                    item.CandidateText = statementText
                    found.Add(item)
                Next
            ElseIf IsWord(token, "GRANT") OrElse IsWord(token, "REVOKE") OrElse IsWord(token, "DENY") Then
                report(index, "Permisos")
            ElseIf token.Kind = TokenKind.Word AndAlso DataWords.Contains(token.Value) Then
                ' ON DELETE / ON UPDATE son clausulas de llaves foraneas, no cambios de datos.
                If index > 0 AndAlso IsWord(tokens(index - 1), "ON") Then Continue For
                report(index, "Datos")
            ElseIf (IsWord(token, "ENABLE") OrElse IsWord(token, "DISABLE")) AndAlso index + 1 < tokens.Count AndAlso IsWord(tokens(index + 1), "TRIGGER") Then
                report(index, "Otro")
            ElseIf (IsWord(token, "EXEC") OrElse IsWord(token, "EXECUTE")) AndAlso CallsProcedure(tokens, index + 1, "sp_rename") Then
                report(index, "Otro")
            End If
        Next
        Return found
    End Function

    ' CREATE/ALTER/DROP de algo que no es procedimiento, vista, funcion, trigger ni sinonimo (tablas, indices, usuarios...).
    Private Shared Sub ReportOtherObject(tokens As List(Of SqlToken), kindIndex As Integer, report As Action(Of Integer, String))
        Dim word As SqlToken = tokens(kindIndex)
        If word.Kind <> TokenKind.Word Then Return
        ' Tablas temporales: son de trabajo y desaparecen solas.
        If IsWord(word, "TABLE") AndAlso kindIndex + 1 < tokens.Count AndAlso tokens(kindIndex + 1).Value.StartsWith("#", StringComparison.Ordinal) Then Return
        Dim category As String = If(StructureWords.Contains(word.Value), "Estructura", If(SecurityWords.Contains(word.Value), "Permisos", "Otro"))
        report(kindIndex - 1, category)
    End Sub

    ' EXEC sp_rename, EXEC sys.sp_rename o EXEC [dbo].[sp_rename].
    Private Shared Function CallsProcedure(tokens As List(Of SqlToken), start As Integer, name As String) As Boolean
        Dim lastToken As Integer
        Dim parts As List(Of String) = ReadName(tokens, start, lastToken)
        Return parts IsNot Nothing AndAlso String.Equals(parts(parts.Count - 1), name, StringComparison.OrdinalIgnoreCase)
    End Function

    ' Lee un nombre de hasta tres partes (base.esquema.objeto) desde "start"; devuelve Nothing si no hay nombre.
    Private Shared Function ReadName(tokens As List(Of SqlToken), start As Integer, ByRef lastToken As Integer) As List(Of String)
        If start >= tokens.Count OrElse Not IsIdentifier(tokens(start)) Then Return Nothing
        Dim position As Integer = start
        Dim parts As New List(Of String) From {tokens(position).Value}
        While position + 2 < tokens.Count AndAlso
              tokens(position + 1).Kind = TokenKind.Dot AndAlso
              IsIdentifier(tokens(position + 2)) AndAlso
              parts.Count < 3
            position += 2
            parts.Add(tokens(position).Value)
        End While
        lastToken = position
        Return parts
    End Function

    Private Shared Function NewDeclared(kind As String, parts As List(Of String), sourceFile As String, start As Integer) As DeclaredObject
        Dim item As New DeclaredObject With {.Kind = kind, .SourceFile = sourceFile, .DeclarationStart = start}
        Select Case parts.Count
            Case 1
                item.ObjectName = parts(0)
            Case 2
                item.SchemaName = parts(0)
                item.ObjectName = parts(1)
            Case Else
                item.DatabaseName = parts(0)
                item.SchemaName = parts(1)
                item.ObjectName = parts(2)
        End Select
        Return item
    End Function

    ' "ON ALL SERVER" justo despues del nombre de un trigger (en CREATE/ALTER o en DROP).
    Private Shared Function IsOnAllServer(tokens As List(Of SqlToken), afterName As Integer) As Boolean
        Return afterName + 2 < tokens.Count AndAlso IsWord(tokens(afterName), "ON") AndAlso
               IsWord(tokens(afterName + 1), "ALL") AndAlso IsWord(tokens(afterName + 2), "SERVER")
    End Function

    Private Shared Function LineNumber(script As String, position As Integer) As Integer
        Dim line As Integer = 1
        For index As Integer = 0 To Math.Min(position, script.Length) - 1
            If script(index) = ChrW(10) Then line += 1
        Next
        Return line
    End Function

    Private Shared Function LineText(script As String, position As Integer) As String
        Dim lineEnd As Integer = script.IndexOfAny({ChrW(10), ChrW(13)}, position)
        If lineEnd < 0 Then lineEnd = script.Length
        Dim text As String = script.Substring(position, lineEnd - position).Trim()
        Return If(text.Length > 160, text.Substring(0, 157) & "...", text)
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
        If IsWord(token, "SYNONYM") Then Return "Sinonimo"
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
