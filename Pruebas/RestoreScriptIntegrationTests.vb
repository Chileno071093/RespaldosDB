Imports System.IO
Imports System.Text
Imports System.Threading
Imports Microsoft.VisualStudio.TestTools.UnitTesting

' Ejecuta de verdad el script de reversion despues de simular una liberacion que modifica y borra objetos.
<TestClass>
Public Class RestoreScriptIntegrationTests
    Private Shared ReadOnly ObjectNames As String() = {"dbo.P_Rev", "dbo.P_QiOff", "dbo.F_Escalar", "dbo.F_Tabla", "dbo.F_Multi", "dbo.V_Rev", "dbo.TR_Rev"}

    <TestMethod>
    Public Sub ScriptDeReversionDevuelveTodoASuDefinicionYConservaPermisos()
        Dim fixture As LocalDbFixture = LocalDbFixture.TryCreate(1)
        If fixture Is Nothing Then Assert.Inconclusive("LocalDB no esta disponible en este equipo.")
        Dim workFolder As String = NewTempFolder("RespaldoReversion")
        Try
            Dim db As String = fixture.Databases(0)
            fixture.ExecScript(db,
                "CREATE TABLE dbo.T (id INT)" & vbCrLf & "GO" & vbCrLf &
                "-- comentario previo que SQL Server guarda" & vbCrLf & "CREATE PROCEDURE dbo.P_Rev AS SELECT 'original' AS v" & vbCrLf & "GO" & vbCrLf &
                "GRANT EXECUTE ON dbo.P_Rev TO public" & vbCrLf & "GO" & vbCrLf &
                "SET QUOTED_IDENTIFIER OFF" & vbCrLf & "GO" & vbCrLf &
                "CREATE PROCEDURE dbo.P_QiOff AS SELECT ""texto con comillas dobles"" AS v" & vbCrLf & "GO" & vbCrLf &
                "SET QUOTED_IDENTIFIER ON" & vbCrLf & "GO" & vbCrLf &
                "CREATE FUNCTION dbo.F_Escalar() RETURNS INT AS BEGIN RETURN 1 END" & vbCrLf & "GO" & vbCrLf &
                "CREATE FUNCTION dbo.F_Tabla() RETURNS TABLE AS RETURN SELECT 1 AS c" & vbCrLf & "GO" & vbCrLf &
                "CREATE FUNCTION dbo.F_Multi() RETURNS @t TABLE (c INT) AS BEGIN INSERT @t VALUES (1) RETURN END" & vbCrLf & "GO" & vbCrLf &
                "CREATE VIEW dbo.V_Rev AS SELECT dbo.F_Escalar() AS c" & vbCrLf & "GO" & vbCrLf &
                "CREATE TRIGGER dbo.TR_Rev ON dbo.T AFTER INSERT AS SET NOCOUNT ON" & vbCrLf & "GO" & vbCrLf &
                "CREATE TRIGGER TRD_Rev ON DATABASE FOR CREATE_TABLE AS SET NOCOUNT ON" & vbCrLf & "GO")

            ' Scripts de la "liberacion" (lo que la app analiza).
            Dim source As String = Path.Combine(workFolder, "src")
            Directory.CreateDirectory(source)
            Dim release As String =
                "ALTER PROCEDURE dbo.P_Rev AS SELECT 'liberacion' AS v" & vbCrLf & "GO" & vbCrLf &
                "ALTER PROCEDURE dbo.P_QiOff AS SELECT 'liberacion' AS v" & vbCrLf & "GO" & vbCrLf &
                "ALTER FUNCTION dbo.F_Escalar() RETURNS INT AS BEGIN RETURN 2 END" & vbCrLf & "GO" & vbCrLf &
                "ALTER FUNCTION dbo.F_Tabla() RETURNS TABLE AS RETURN SELECT 2 AS c" & vbCrLf & "GO" & vbCrLf &
                "ALTER FUNCTION dbo.F_Multi() RETURNS @t TABLE (c INT) AS BEGIN INSERT @t VALUES (2) RETURN END" & vbCrLf & "GO" & vbCrLf &
                "ALTER VIEW dbo.V_Rev AS SELECT 2 AS c" & vbCrLf & "GO" & vbCrLf &
                "ALTER TRIGGER dbo.TR_Rev ON dbo.T AFTER INSERT AS SELECT 1" & vbCrLf & "GO" & vbCrLf &
                "ALTER TRIGGER TRD_Rev ON DATABASE FOR CREATE_TABLE AS SELECT 1" & vbCrLf & "GO"
            File.WriteAllText(Path.Combine(source, "liberacion.sql"), release, New UTF8Encoding(False))

            Dim original As Dictionary(Of String, String) = ObjectNames.ToDictionary(Function(x) x, Function(x) fixture.Definition(db, x))
            Dim originalDdl As String = CStr(fixture.Scalar(db, "SELECT m.definition FROM sys.sql_modules m JOIN sys.triggers t ON t.object_id = m.object_id WHERE t.parent_class = 0 AND t.name = 'TRD_Rev'"))

            Dim request As BackupRequest = fixture.NewRequest({db}, source, Path.Combine(workFolder, "out"))
            Dim analysis As BackupAnalysis = BackupService.Analyze(request, CancellationToken.None, Nothing)
            Assert.AreEqual(8, analysis.Items.Count)
            Assert.IsTrue(analysis.Items.All(Function(x) x.CanBackup), String.Join(" | ", analysis.Items.Select(Function(x) x.Declaration.ObjectName & ":" & x.Status)))
            Assert.IsTrue(analysis.Items.All(Function(x) x.HasChanges), "Todos los scripts de la liberacion cambian algo")
            Dim result As BackupResult = BackupService.Save(request, analysis, analysis.Items.Select(Function(x) x.Key), CancellationToken.None, Nothing)
            Dim restoreScript As String = File.ReadAllText(Path.Combine(result.Folder, result.RestoreScripts.Single()))

            ' Liberacion: modifica unos objetos y borra otros.
            fixture.ExecScript(db,
                "ALTER PROCEDURE dbo.P_Rev AS SELECT 'liberacion' AS v" & vbCrLf & "GO" & vbCrLf &
                "ALTER FUNCTION dbo.F_Escalar() RETURNS INT AS BEGIN RETURN 2 END" & vbCrLf & "GO" & vbCrLf &
                "DROP VIEW dbo.V_Rev" & vbCrLf & "GO" & vbCrLf &
                "DROP FUNCTION dbo.F_Multi" & vbCrLf & "GO" & vbCrLf &
                "DROP TRIGGER dbo.TR_Rev" & vbCrLf & "GO" & vbCrLf &
                "DROP TRIGGER TRD_Rev ON DATABASE" & vbCrLf & "GO" & vbCrLf &
                "DROP PROCEDURE dbo.P_QiOff" & vbCrLf & "GO")
            Assert.AreNotEqual(original("dbo.P_Rev"), fixture.Definition(db, "dbo.P_Rev"))

            ' Se ejecuta dos veces: debe ser repetible.
            For run As Integer = 1 To 2
                fixture.ExecScript("master", restoreScript)
                For Each name As String In ObjectNames
                    Assert.AreEqual(original(name), fixture.Definition(db, name), "Definicion distinta de " & name & " en la ejecucion " & run.ToString())
                Next
                Assert.AreEqual(originalDdl, CStr(fixture.Scalar(db, "SELECT m.definition FROM sys.sql_modules m JOIN sys.triggers t ON t.object_id = m.object_id WHERE t.parent_class = 0 AND t.name = 'TRD_Rev'")))
                Assert.AreEqual(0, CInt(fixture.Scalar(db, "SELECT uses_quoted_identifier FROM sys.sql_modules WHERE object_id = OBJECT_ID('dbo.P_QiOff')")), "P_QiOff debe conservar QUOTED_IDENTIFIER OFF")
                Assert.AreEqual(1, CInt(fixture.Scalar(db, "SELECT COUNT(*) FROM sys.database_permissions WHERE major_id = OBJECT_ID('dbo.P_Rev') AND permission_name = 'EXECUTE'")), "Se perdio el GRANT de P_Rev")
                Assert.AreEqual(CInt(fixture.Scalar(db, "SELECT OBJECT_ID('dbo.T')")), CInt(fixture.Scalar(db, "SELECT parent_id FROM sys.triggers WHERE name = 'TR_Rev'")), "TR_Rev debe volver a la tabla dbo.T")
            Next
            Assert.AreEqual("texto con comillas dobles", CStr(fixture.Scalar(db, "EXEC dbo.P_QiOff")))
        Finally
            fixture.Dispose()
            TryDeleteFolder(workFolder)
        End Try
    End Sub
End Class
