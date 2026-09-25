Imports System.Text
Imports Microsoft.VisualStudio.TestTools.UnitTesting

<TestClass>
Public Class DecodeScriptTests
    Private Const Sample As String = "CREATE PROCEDURE dbo.P_Año AS SELECT 'acción'"

    <TestMethod>
    Public Sub Ansi1252SinBom()
        Assert.AreEqual(Sample, BackupService.DecodeScript(Encoding.GetEncoding(1252).GetBytes(Sample)))
    End Sub

    <TestMethod>
    Public Sub Utf8SinBom()
        Assert.AreEqual(Sample, BackupService.DecodeScript(New UTF8Encoding(False).GetBytes(Sample)))
    End Sub

    <TestMethod>
    Public Sub Utf8ConBom()
        Assert.AreEqual(Sample, BackupService.DecodeScript(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(Sample)).ToArray()))
    End Sub

    <TestMethod>
    Public Sub Utf16LeConBom()
        Assert.AreEqual(Sample, BackupService.DecodeScript(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(Sample)).ToArray()))
    End Sub

    <TestMethod>
    Public Sub ArchivoVacio()
        Assert.AreEqual("", BackupService.DecodeScript(New Byte() {}))
    End Sub
End Class

<TestClass>
Public Class SqlObjectParserTests
    <TestMethod>
    Public Sub ReconoceCreateAlterYCreateOrAlter()
        Dim script As String = "CREATE PROCEDURE dbo.P_A AS SELECT 1" & vbCrLf & "GO" & vbCrLf &
                               "ALTER VIEW [Rep].[V_B] AS SELECT 1 AS x" & vbCrLf & "GO" & vbCrLf &
                               "CREATE OR ALTER FUNCTION F_C() RETURNS INT AS BEGIN RETURN 1 END" & vbCrLf & "GO" & vbCrLf &
                               "CREATE TRIGGER dbo.TR_D ON dbo.T AFTER INSERT AS RETURN"
        Dim found As List(Of DeclaredObject) = SqlObjectParser.Parse(script, "x.sql")
        CollectionAssert.AreEqual({"Procedimiento", "Vista", "Funcion", "Trigger"}, found.Select(Function(x) x.Kind).ToArray())
        Assert.AreEqual("Rep", found(1).SchemaName)
        Assert.AreEqual("V_B", found(1).ObjectName)
        Assert.IsNull(found(2).SchemaName)
        Assert.AreEqual("F_C", found(2).ObjectName)
    End Sub

    <TestMethod>
    Public Sub CreateOrAlterEsUnaSolaDeclaracion()
        Dim found As DeclaredObject = SqlObjectParser.Parse("CREATE OR ALTER PROCEDURE dbo.P AS SELECT 1", "x.sql").Single()
        Assert.AreEqual(0, found.DeclarationStart)
        StringAssert.StartsWith(found.CandidateText, "CREATE OR ALTER")
    End Sub

    <TestMethod>
    Public Sub IgnoraComentariosYCadenas()
        Dim script As String = "-- CREATE PROCEDURE dbo.Falso1 AS SELECT 1" & vbCrLf &
                               "/* CREATE VIEW dbo.Falso2 AS SELECT 1 */" & vbCrLf &
                               "CREATE PROCEDURE dbo.P_Real AS EXEC('CREATE PROCEDURE dbo.Falso3 AS SELECT 1')"
        Dim found As List(Of DeclaredObject) = SqlObjectParser.Parse(script, "x.sql")
        Assert.AreEqual(1, found.Count)
        Assert.AreEqual("P_Real", found(0).ObjectName)
    End Sub

    <TestMethod>
    Public Sub NombreDeTresPartesYCorchetesEscapados()
        Dim found As DeclaredObject = SqlObjectParser.Parse("CREATE PROCEDURE [Base].[dbo].[P_]]raro] AS SELECT 1", "x.sql").Single()
        Assert.AreEqual("Base", found.DatabaseName)
        Assert.AreEqual("dbo", found.SchemaName)
        Assert.AreEqual("P_]raro", found.ObjectName)
    End Sub

    <TestMethod>
    Public Sub CandidateTextTerminaEnGoYDeclarationStartApuntaAlCreate()
        Dim script As String = "-- cabecera" & vbCrLf & "CREATE PROCEDURE dbo.P AS SELECT 1" & vbCrLf & "GO" & vbCrLf & "SELECT 2"
        Dim found As DeclaredObject = SqlObjectParser.Parse(script, "x.sql").Single()
        Assert.AreEqual("CREATE PROCEDURE dbo.P AS SELECT 1", found.CandidateText)
        Assert.AreEqual(script.IndexOf("CREATE", StringComparison.Ordinal), found.DeclarationStart)
    End Sub
End Class
