Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports System.Text
Imports System.Text.RegularExpressions
Imports Microsoft.VisualBasic

Friend Enum DiffKind
    Same
    Removed
    Added
    Skipped
End Enum

Friend NotInheritable Class DiffLine
    Public Property Kind As DiffKind
    Public Property OldNumber As Integer
    Public Property NewNumber As Integer
    Public Property Text As String
End Class

Friend NotInheritable Class DiffResult
    Public Property Lines As List(Of DiffLine)
    Public Property Removed As Integer
    Public Property Added As Integer
    Public Property Truncated As Boolean

    Public ReadOnly Property HasDifferences As Boolean
        Get
            Return Removed > 0 OrElse Added > 0
        End Get
    End Property
End Class

Friend NotInheritable Class DiffService
    ' Limite de ediciones para Myers (memoria ~ MaxEdits^2 enteros); por encima se muestra el resto como reemplazo completo.
    Private Const MaxEdits As Integer = 2000
    Private Shared ReadOnly LeadingKeyword As New Regex("^(?:CREATE\s+OR\s+ALTER|ALTER|CREATE)\s+(?<kind>PROCEDURE|PROC|VIEW|TRIGGER|FUNCTION|SYNONYM)\b", RegexOptions.IgnoreCase)
    Private Shared ReadOnly Whitespace As New Regex("\s+")

    Private Sub New()
    End Sub

    ' Deja solo la declaracion: quita comentarios previos al CREATE/ALTER (SQL Server los guarda en sys.sql_modules)
    ' y unifica el encabezado (CREATE / ALTER / CREATE OR ALTER, mayusculas y PROC/PROCEDURE),
    ' porque SQL Server guarda un ALTER como CREATE y un CREATE OR ALTER como CREATE con espacios de relleno.
    Public Shared Function DeclarationText(definition As String) As String
        If definition Is Nothing Then Return ""
        Dim text As String = definition
        Dim declaration As DeclaredObject = SqlObjectParser.Parse(definition, Nothing).FirstOrDefault(Function(x) Not x.IsDrop)
        If declaration IsNot Nothing Then text = declaration.CandidateText
        text = LeadingKeyword.Replace(text.Trim(), AddressOf NormalizedHeader, 1)
        ' Sinonimos: SQL Server devuelve el nombre y el destino con corchetes; se comparan sin ellos.
        If text.StartsWith("CREATE SYNONYM", StringComparison.Ordinal) Then
            text = Whitespace.Replace(text.Replace("[", "").Replace("]", ""), " ").Trim().TrimEnd(";"c)
        End If
        Return text
    End Function

    Private Shared Function NormalizedHeader(match As Match) As String
        Dim kind As String = match.Groups("kind").Value.ToUpperInvariant()
        Return "CREATE " & If(kind = "PROC", "PROCEDURE", kind)
    End Function

    Public Shared Function SplitLines(value As String) As String()
        Return value.Replace(vbCrLf, vbLf).Replace(vbCr, vbLf).TrimEnd(ChrW(10)).Split({vbLf}, StringSplitOptions.None)
    End Function

    Public Shared Function Compare(currentDefinition As String, candidateDefinition As String, ignoreWhitespace As Boolean) As DiffResult
        Dim oldLines As String() = SplitLines(DeclarationText(currentDefinition))
        Dim newLines As String() = SplitLines(DeclarationText(candidateDefinition))
        Return Compute(oldLines, newLines, ignoreWhitespace)
    End Function

    Public Shared Function Compute(oldLines As String(), newLines As String(), ignoreWhitespace As Boolean) As DiffResult
        Dim oldKeys As String() = Array.ConvertAll(oldLines, Function(x) LineKey(x, ignoreWhitespace))
        Dim newKeys As String() = Array.ConvertAll(newLines, Function(x) LineKey(x, ignoreWhitespace))
        Dim result As New DiffResult With {.Lines = New List(Of DiffLine)()}

        Dim prefix As Integer = 0
        While prefix < oldKeys.Length AndAlso prefix < newKeys.Length AndAlso oldKeys(prefix) = newKeys(prefix)
            prefix += 1
        End While
        Dim suffix As Integer = 0
        While suffix < oldKeys.Length - prefix AndAlso suffix < newKeys.Length - prefix AndAlso
              oldKeys(oldKeys.Length - 1 - suffix) = newKeys(newKeys.Length - 1 - suffix)
            suffix += 1
        End While

        For index As Integer = 0 To prefix - 1
            AddLine(result, DiffKind.Same, index, index, newLines(index))
        Next
        Dim script As List(Of DiffKind) = Myers(oldKeys, prefix, oldKeys.Length - suffix, newKeys, prefix, newKeys.Length - suffix, result.Truncated)
        Dim oldIndex As Integer = prefix
        Dim newIndex As Integer = prefix
        For Each kind As DiffKind In script
            Select Case kind
                Case DiffKind.Same
                    AddLine(result, DiffKind.Same, oldIndex, newIndex, newLines(newIndex))
                    oldIndex += 1
                    newIndex += 1
                Case DiffKind.Removed
                    AddLine(result, DiffKind.Removed, oldIndex, -1, oldLines(oldIndex))
                    oldIndex += 1
                Case DiffKind.Added
                    AddLine(result, DiffKind.Added, -1, newIndex, newLines(newIndex))
                    newIndex += 1
            End Select
        Next
        For index As Integer = 0 To suffix - 1
            AddLine(result, DiffKind.Same, oldIndex + index, newIndex + index, newLines(newIndex + index))
        Next
        Return result
    End Function

    ' Oculta los tramos iguales largos y deja "context" lineas alrededor de cada cambio.
    Public Shared Function Collapse(lines As List(Of DiffLine), context As Integer) As List(Of DiffLine)
        Dim keep(lines.Count - 1) As Boolean
        For index As Integer = 0 To lines.Count - 1
            If lines(index).Kind = DiffKind.Same Then Continue For
            For near As Integer = Math.Max(0, index - context) To Math.Min(lines.Count - 1, index + context)
                keep(near) = True
            Next
        Next
        Dim output As New List(Of DiffLine)()
        Dim index2 As Integer = 0
        While index2 < lines.Count
            If keep(index2) Then
                output.Add(lines(index2))
                index2 += 1
            Else
                Dim start As Integer = index2
                While index2 < lines.Count AndAlso Not keep(index2)
                    index2 += 1
                End While
                output.Add(New DiffLine With {.Kind = DiffKind.Skipped, .Text = "... " & (index2 - start).ToString() & " lineas iguales ..."})
            End If
        End While
        Return output
    End Function

    Private Shared Sub AddLine(result As DiffResult, kind As DiffKind, oldIndex As Integer, newIndex As Integer, text As String)
        result.Lines.Add(New DiffLine With {.Kind = kind, .OldNumber = oldIndex + 1, .NewNumber = newIndex + 1, .Text = text})
        If kind = DiffKind.Removed Then result.Removed += 1
        If kind = DiffKind.Added Then result.Added += 1
    End Sub

    Private Shared Function LineKey(line As String, ignoreWhitespace As Boolean) As String
        If ignoreWhitespace Then Return Whitespace.Replace(line, " ").Trim()
        Return line.TrimEnd()
    End Function

    ' Algoritmo de Myers (O((N+M)D)) sobre el tramo [oldStart, oldEnd) x [newStart, newEnd).
    Private Shared Function Myers(a As String(), oldStart As Integer, oldEnd As Integer,
                                  b As String(), newStart As Integer, newEnd As Integer,
                                  ByRef truncated As Boolean) As List(Of DiffKind)
        Dim n As Integer = oldEnd - oldStart
        Dim m As Integer = newEnd - newStart
        Dim script As New List(Of DiffKind)()
        If n = 0 AndAlso m = 0 Then Return script

        Dim maxD As Integer = Math.Min(n + m, MaxEdits)
        Dim offset As Integer = maxD + 1
        Dim v(2 * offset) As Integer
        Dim trace As New List(Of Integer())()
        Dim finalD As Integer = -1
        For d As Integer = 0 To maxD
            Dim snapshot(2 * d + 2) As Integer
            Array.Copy(v, offset - d - 1, snapshot, 0, 2 * d + 3)
            trace.Add(snapshot)
            For k As Integer = -d To d Step 2
                Dim x As Integer
                If k = -d OrElse (k <> d AndAlso v(offset + k - 1) < v(offset + k + 1)) Then
                    x = v(offset + k + 1)
                Else
                    x = v(offset + k - 1) + 1
                End If
                Dim y As Integer = x - k
                While x < n AndAlso y < m AndAlso a(oldStart + x) = b(newStart + y)
                    x += 1
                    y += 1
                End While
                v(offset + k) = x
                If x >= n AndAlso y >= m Then
                    finalD = d
                    Exit For
                End If
            Next
            If finalD >= 0 Then Exit For
        Next

        If finalD < 0 Then
            ' Demasiado distintos: se muestra todo el tramo como eliminado y agregado.
            truncated = True
            For index As Integer = 1 To n
                script.Add(DiffKind.Removed)
            Next
            For index As Integer = 1 To m
                script.Add(DiffKind.Added)
            Next
            Return script
        End If

        Dim cx As Integer = n
        Dim cy As Integer = m
        For d As Integer = finalD To 0 Step -1
            Dim snapshot As Integer() = trace(d)
            Dim k As Integer = cx - cy
            Dim prevK As Integer
            If k = -d OrElse (k <> d AndAlso snapshot(k - 1 + d + 1) < snapshot(k + 1 + d + 1)) Then
                prevK = k + 1
            Else
                prevK = k - 1
            End If
            Dim prevX As Integer = snapshot(prevK + d + 1)
            Dim prevY As Integer = prevX - prevK
            While cx > prevX AndAlso cy > prevY
                script.Add(DiffKind.Same)
                cx -= 1
                cy -= 1
            End While
            If d > 0 Then
                If cx = prevX Then
                    script.Add(DiffKind.Added)
                    cy -= 1
                Else
                    script.Add(DiffKind.Removed)
                    cx -= 1
                End If
            End If
        Next
        script.Reverse()
        Return script
    End Function
End Class
