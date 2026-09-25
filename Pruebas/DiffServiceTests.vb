Imports System.Diagnostics
Imports System.Windows.Forms
Imports Microsoft.VisualStudio.TestTools.UnitTesting

<TestClass>
Public Class DiffServiceTests
    Private Const Stored As String = "-- comentario que SQL Server guarda" & vbCrLf & "/* bloque */" & vbCrLf & "CREATE   PROCEDURE dbo.P_A" & vbCrLf & "AS" & vbCrLf & "SELECT 1"

    Private Shared Function Lcs(a As String(), b As String()) As Integer
        Dim dp(a.Length, b.Length) As Integer
        For i As Integer = a.Length - 1 To 0 Step -1
            For j As Integer = b.Length - 1 To 0 Step -1
                dp(i, j) = If(a(i) = b(j), dp(i + 1, j + 1) + 1, Math.Max(dp(i + 1, j), dp(i, j + 1)))
            Next
        Next
        Return dp(0, 0)
    End Function

    ' Reconstruye ambos lados a partir del diff y verifica la numeracion.
    Private Shared Function Consistent(result As DiffResult, a As String(), b As String()) As Boolean
        Dim oldSide As New List(Of String)()
        Dim newSide As New List(Of String)()
        For Each line As DiffLine In result.Lines
            If line.Kind = DiffKind.Same OrElse line.Kind = DiffKind.Removed Then
                If line.OldNumber <> oldSide.Count + 1 Then Return False
                oldSide.Add(If(line.Kind = DiffKind.Same, a(line.OldNumber - 1), line.Text))
            End If
            If line.Kind = DiffKind.Same OrElse line.Kind = DiffKind.Added Then
                If line.NewNumber <> newSide.Count + 1 Then Return False
                newSide.Add(line.Text)
            End If
        Next
        Return oldSide.SequenceEqual(a) AndAlso newSide.SequenceEqual(b)
    End Function

    <TestMethod>
    Public Sub MyersEsMinimoYConsistenteEnCasosAleatorios()
        Dim random As New Random(12345)
        For iteration As Integer = 1 To 2000
            Dim a As String() = Enumerable.Range(0, random.Next(0, 25)).Select(Function(x) "L" & random.Next(0, 6).ToString()).ToArray()
            Dim b As String() = Enumerable.Range(0, random.Next(0, 25)).Select(Function(x) "L" & random.Next(0, 6).ToString()).ToArray()
            Dim result As DiffResult = DiffService.Compute(a, b, False)
            Assert.IsTrue(Consistent(result, a, b), "Diff inconsistente en el caso " & iteration.ToString())
            Assert.AreEqual(a.Length + b.Length - 2 * Lcs(a, b), result.Removed + result.Added, "Diff no minimo en el caso " & iteration.ToString())
        Next
    End Sub

    <TestMethod>
    Public Sub BloqueMovido()
        Dim baseLines As String() = Enumerable.Range(1, 40).Select(Function(x) "linea " & x.ToString()).ToArray()
        Dim moved As New List(Of String)(baseLines)
        Dim block As List(Of String) = moved.GetRange(5, 5)
        moved.RemoveRange(5, 5)
        moved.InsertRange(30, block)
        Dim result As DiffResult = DiffService.Compute(baseLines, moved.ToArray(), False)
        Assert.AreEqual(5, result.Removed)
        Assert.AreEqual(5, result.Added)
    End Sub

    <TestMethod>
    <DataRow("ALTER PROCEDURE")>
    <DataRow("CREATE OR ALTER PROCEDURE")>
    <DataRow("CREATE PROCEDURE")>
    <DataRow("create procedure")>
    <DataRow("alter proc")>
    <DataRow("Create Or Alter Proc")>
    Public Sub EncabezadoYComentariosPreviosNoCuentanComoDiferencia(header As String)
        Dim scriptText As String = header & " dbo.P_A" & vbCrLf & "AS" & vbCrLf & "SELECT 1   "
        Assert.IsFalse(DiffService.Compare(Stored, scriptText, False).HasDifferences)
    End Sub

    <TestMethod>
    Public Sub CambioRealSeDetecta()
        Dim result As DiffResult = DiffService.Compare(Stored, "ALTER PROCEDURE dbo.P_A" & vbCrLf & "AS" & vbCrLf & "SELECT 2", False)
        Assert.AreEqual(1, result.Removed)
        Assert.AreEqual(1, result.Added)
        Assert.AreEqual("SELECT 2", result.Lines.Last().Text)
    End Sub

    <TestMethod>
    Public Sub OpcionIgnorarEspacios()
        Dim spaced As String = "CREATE PROCEDURE dbo.P_A" & vbCrLf & "AS" & vbCrLf & vbTab & "SELECT   1"
        Assert.IsTrue(DiffService.Compare(Stored, spaced, False).HasDifferences)
        Assert.IsFalse(DiffService.Compare(Stored, spaced, True).HasDifferences)
    End Sub

    <TestMethod>
    Public Sub ColapsarDejaContexto()
        Dim lines As String() = Enumerable.Range(1, 100).Select(Function(x) "x" & x.ToString()).ToArray()
        Dim changed As String() = CType(lines.Clone(), String())
        changed(49) = "CAMBIO"
        Dim collapsed As List(Of DiffLine) = DiffService.Collapse(DiffService.Compute(lines, changed, False).Lines, 3)
        Assert.AreEqual(2, collapsed.Where(Function(x) x.Kind = DiffKind.Skipped).Count())
        Assert.AreEqual(2 + 3 + 2 + 3, collapsed.Count)
    End Sub

    <TestMethod>
    Public Sub RendimientoYLimiteDeEdiciones()
        Dim large As String() = Enumerable.Range(1, 20000).Select(Function(x) "fila " & x.ToString()).ToArray()
        Dim edited As String() = CType(large.Clone(), String())
        For index As Integer = 0 To 9
            edited(index * 1999) = "editada " & index.ToString()
        Next
        Dim watch As Stopwatch = Stopwatch.StartNew()
        Dim result As DiffResult = DiffService.Compute(large, edited, False)
        Assert.AreEqual(10, result.Removed)
        Assert.IsFalse(result.Truncated)
        Assert.IsTrue(watch.ElapsedMilliseconds < 2000, "Demasiado lento: " & watch.ElapsedMilliseconds.ToString() & " ms")

        Dim first As String() = large.Take(5000).ToArray()
        Dim other As String() = Enumerable.Range(1, 5000).Select(Function(x) "otra " & x.ToString()).ToArray()
        Dim truncated As DiffResult = DiffService.Compute(first, other, False)
        Assert.IsTrue(truncated.Truncated)
        Assert.IsTrue(Consistent(truncated, first, other))
    End Sub

    <STATestMethod>
    Public Sub RtfConCaracteresEspeciales()
        Dim tricky As String = "CREATE PROCEDURE dbo.P_X AS" & vbCrLf & "SELECT '{llaves}\barra' , N'ñandú €'" & vbTab & "-- fin"
        Dim result As DiffResult = DiffService.Compare("CREATE PROCEDURE dbo.P_X AS" & vbCrLf & "SELECT 1", tricky, False)
        Using box As New RichTextBox()
            box.Rtf = CompareForm.BuildRtf(result, result.Lines, False)
            StringAssert.Contains(box.Text, "+ SELECT '{llaves}\barra' , N'ñandú €'" & vbTab & "-- fin")
            StringAssert.Contains(box.Text, "- SELECT 1")
        End Using
    End Sub

    <STATestMethod>
    Public Sub CompararConDuplicadosMuestraUnaPestanaPorArchivo()
        Dim item As New AnalysisItem With {
            .DatabaseName = "Base", .Status = "Listo",
            .Current = New CatalogObject With {.Definition = "-- c" & vbCrLf & "CREATE PROCEDURE dbo.P_D AS SELECT 1"},
            .Declaration = New DeclaredObject With {.Kind = "Procedimiento", .SchemaName = "dbo", .ObjectName = "P_D", .SourceFile = "C:\x\uno.sql", .CandidateText = "ALTER PROCEDURE dbo.P_D AS SELECT 1"},
            .AlsoDeclaredIn = New List(Of DeclaredObject) From {New DeclaredObject With {.Kind = "Procedimiento", .SchemaName = "dbo", .ObjectName = "P_D", .SourceFile = "C:\x\dos.sql", .CandidateText = "ALTER PROCEDURE dbo.P_D AS SELECT 2"}}
        }
        Using form As New CompareForm(item)
            Dim tabs As TabControl = form.Controls.OfType(Of TableLayoutPanel)().First().Controls.OfType(Of TabControl)().First()
            Dim titles As String() = tabs.TabPages.Cast(Of TabPage)().Select(Function(p) p.Text).ToArray()
            CollectionAssert.AreEqual({"Diferencias - uno.sql (iguales)", "Diferencias - dos.sql (-1 +1)", "Actual en SQL Server", "Archivo de liberacion - uno.sql", "Archivo de liberacion - dos.sql"}, titles)
            Assert.AreEqual("Comparar [Base] Procedimiento [dbo].[P_D]", form.Text)
        End Using
    End Sub
End Class
