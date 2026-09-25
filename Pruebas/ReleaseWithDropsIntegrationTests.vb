Imports System.IO
Imports System.Text
Imports System.Threading
Imports Microsoft.VisualStudio.TestTools.UnitTesting

' Liberacion con DROP, DROP + CREATE, sinonimos, un trigger de servidor y sentencias que no tienen reversion:
' se analiza, se respalda, se aplica la liberacion y se ejecutan los scripts de reversion.
<TestClass>
Public Class ReleaseWithDropsIntegrationTests
    <TestMethod>
    Public Sub DropsSinonimosYTriggerDeServidorSeRespaldanYSeRevierten()
        Dim fixture As LocalDbFixture = LocalDbFixture.TryCreate(1)
        If fixture Is Nothing Then Assert.Inconclusive("LocalDB no esta disponible en este equipo.")
        Dim workFolder As String = NewTempFolder("RespaldoDrops")
        Dim serverTrigger As String = "TRS_Prueba_" & Guid.NewGuid().ToString("N").Substring(0, 8)
        Try
            Dim db As String = fixture.Databases(0)
            fixture.ExecScript(db,
                "CREATE TABLE dbo.T (id INT)" & vbCrLf & "GO" & vbCrLf &
                "CREATE TABLE dbo.T2 (id INT)" & vbCrLf & "GO" & vbCrLf &
                "CREATE PROCEDURE dbo.P_Borrar AS SELECT 'original' AS v" & vbCrLf & "GO" & vbCrLf &
                "CREATE PROCEDURE dbo.P_Recrear AS SELECT 1 AS v" & vbCrLf & "GO" & vbCrLf &
                "CREATE SYNONYM dbo.S_T FOR dbo.T" & vbCrLf & "GO")
            fixture.Exec("master", "CREATE TRIGGER [" & serverTrigger & "] ON ALL SERVER FOR CREATE_ENDPOINT AS SET NOCOUNT ON")
            ' Sin VIEW ANY DEFINITION un login no ve los triggers de servidor.
            fixture.Exec("master", "GRANT VIEW ANY DEFINITION TO [" & fixture.Login & "]")

            Dim release As String =
                "IF OBJECT_ID('dbo.P_Borrar') IS NOT NULL DROP PROCEDURE dbo.P_Borrar" & vbCrLf & "GO" & vbCrLf &
                "DROP PROCEDURE IF EXISTS dbo.P_Recrear" & vbCrLf & "GO" & vbCrLf &
                "CREATE PROCEDURE dbo.P_Recrear AS SELECT 2 AS v" & vbCrLf & "GO" & vbCrLf &
                "DROP SYNONYM dbo.S_T" & vbCrLf & "GO" & vbCrLf &
                "CREATE SYNONYM dbo.S_T FOR dbo.T2" & vbCrLf & "GO" & vbCrLf &
                "ALTER TRIGGER [" & serverTrigger & "] ON ALL SERVER FOR CREATE_ENDPOINT AS SELECT 1" & vbCrLf & "GO" & vbCrLf &
                "ALTER TABLE dbo.T ADD c2 INT" & vbCrLf & "GO" & vbCrLf &
                "INSERT INTO dbo.T (id) VALUES (1)" & vbCrLf & "GO" & vbCrLf &
                "GRANT EXECUTE ON dbo.P_Recrear TO public" & vbCrLf & "GO"
            Dim source As String = Path.Combine(workFolder, "src")
            Directory.CreateDirectory(source)
            File.WriteAllText(Path.Combine(source, "liberacion.sql"), release, New UTF8Encoding(False))

            Dim originalBorrar As String = fixture.Definition(db, "dbo.P_Borrar")
            Dim originalRecrear As String = fixture.Definition(db, "dbo.P_Recrear")
            Dim originalTrigger As String = CStr(fixture.Scalar("master", "SELECT m.definition FROM sys.server_sql_modules m JOIN sys.server_triggers t ON t.object_id = m.object_id WHERE t.name = N'" & serverTrigger & "'"))

            ' ---- Analisis ----
            Dim request As BackupRequest = fixture.NewRequest({db}, source, Path.Combine(workFolder, "out"))
            Dim analysis As BackupAnalysis = BackupService.Analyze(request, CancellationToken.None, Nothing)
            Dim row As Func(Of String, AnalysisItem) = Function(name) analysis.Items.Single(Function(x) x.Declaration.ObjectName = name)
            Assert.AreEqual(4, analysis.Items.Count, String.Join(" | ", analysis.Items.Select(Function(x) x.Declaration.DisplayName() & ":" & x.Status)))
            Assert.IsTrue(analysis.Items.All(Function(x) x.CanBackup), String.Join(" | ", analysis.Items.Select(Function(x) x.Declaration.ObjectName & ":" & x.Status & ":" & x.Detail)))

            Dim borrar As AnalysisItem = row("P_Borrar")
            Assert.IsTrue(borrar.Declaration.IsDrop)
            Assert.AreEqual("Se elimina", borrar.ChangeSummary)
            StringAssert.Contains(borrar.Detail, "ELIMINA")

            Dim recrear As AnalysisItem = row("P_Recrear")
            Assert.IsFalse(recrear.Declaration.IsDrop)
            Assert.IsTrue(recrear.Declaration.AlsoDropped)
            Assert.AreEqual("-1 +1", recrear.ChangeSummary)
            StringAssert.Contains(recrear.Detail, "se pierden sus permisos")

            Dim synonym As AnalysisItem = row("S_T")
            Assert.AreEqual("Sinonimo", synonym.Declaration.Kind)
            Assert.AreEqual("-1 +1", synonym.ChangeSummary)

            Dim trigger As AnalysisItem = row(serverTrigger)
            Assert.AreEqual(BackupService.ServerScope, trigger.DatabaseName)
            Assert.IsTrue(trigger.HasChanges)

            CollectionAssert.AreEqual({"Estructura", "Datos", "Permisos"}, analysis.UnsupportedStatements.Select(Function(x) x.Category).ToArray(), String.Join(" | ", analysis.UnsupportedStatements))

            ' ---- Respaldo ----
            Dim result As BackupResult = BackupService.Save(request, analysis, analysis.Items.Select(Function(x) x.Key), CancellationToken.None, Nothing)
            Assert.IsTrue(File.Exists(Path.Combine(result.Folder, db, "Procedimientos", "[dbo].[P_Borrar]_" & db & ".sql")))
            Assert.IsTrue(File.Exists(Path.Combine(result.Folder, db, "Sinonimos", "[dbo].[S_T]_" & db & ".sql")))
            Dim triggerFile As String = File.ReadAllText(Path.Combine(result.Folder, "Servidor", "Triggers", "[" & serverTrigger & "]_servidor.sql"))
            StringAssert.StartsWith(triggerFile, "USE [master]")
            CollectionAssert.AreEquivalent({Path.Combine(db, "Restaurar_" & db & ".sql"), Path.Combine("Servidor", "Restaurar_servidor.sql")}, result.RestoreScripts)
            Dim report As String = File.ReadAllText(Path.Combine(result.Folder, BackupService.UnsupportedReportName))
            StringAssert.Contains(report, "ALTER TABLE dbo.T ADD c2 INT")
            StringAssert.Contains(report, "INSERT INTO dbo.T (id) VALUES (1)")
            StringAssert.Contains(File.ReadAllText(Path.Combine(result.Folder, result.RestoreScripts(0))), "3 sentencia(s) que este script NO deshace")

            ' ---- Se aplica la liberacion ----
            fixture.ExecScript(db, release)
            Assert.AreEqual(DBNull.Value, fixture.Scalar(db, "SELECT OBJECT_ID('dbo.P_Borrar')"), "La liberacion debe haber borrado P_Borrar")
            Assert.AreEqual("[dbo].[T2]", CStr(fixture.Scalar(db, "SELECT base_object_name FROM sys.synonyms WHERE name = 'S_T'")))

            ' ---- Reversion ----
            For Each restorePath As String In result.RestoreScripts
                fixture.ExecScript("master", File.ReadAllText(Path.Combine(result.Folder, restorePath)))
            Next
            Assert.AreEqual(originalBorrar, fixture.Definition(db, "dbo.P_Borrar"), "P_Borrar debe volver a existir con su definicion")
            Assert.AreEqual(originalRecrear, fixture.Definition(db, "dbo.P_Recrear"))
            Assert.AreEqual("[dbo].[T]", CStr(fixture.Scalar(db, "SELECT base_object_name FROM sys.synonyms WHERE name = 'S_T'")))
            Assert.AreEqual(originalTrigger, CStr(fixture.Scalar("master", "SELECT m.definition FROM sys.server_sql_modules m JOIN sys.server_triggers t ON t.object_id = m.object_id WHERE t.name = N'" & serverTrigger & "'")))
        Finally
            Try
                fixture.Exec("master", "IF EXISTS (SELECT 1 FROM sys.server_triggers WHERE name = N'" & serverTrigger & "') DROP TRIGGER [" & serverTrigger & "] ON ALL SERVER")
            Catch ex As Exception
            End Try
            fixture.Dispose()
            TryDeleteFolder(workFolder)
        End Try
    End Sub
End Class
