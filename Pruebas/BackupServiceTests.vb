Imports System.Diagnostics
Imports System.IO
Imports System.Reflection
Imports System.Text
Imports System.Threading
Imports Microsoft.Data.SqlClient
Imports Microsoft.VisualStudio.TestTools.UnitTesting

' Pruebas de integracion contra LocalDB con un login SQL propio (mismo camino que en produccion).
<TestClass>
Public Class BackupServiceTests
    Private Shared fixture As LocalDbFixture
    Private Shared db1 As String
    Private Shared db2 As String
    Private workFolder As String
    Private sourceFolder As String

    <ClassInitialize>
    Public Shared Sub ClassSetup(context As TestContext)
        fixture = LocalDbFixture.TryCreate(2)
        If fixture Is Nothing Then Return
        db1 = fixture.Databases(0)
        db2 = fixture.Databases(1)
        fixture.Exec(db1, "CREATE PROCEDURE dbo.P_Año AS SELECT N'ñ'")
        fixture.Exec(db1, "CREATE PROCEDURE dbo.P_Uno AS SELECT 1")
        fixture.Exec(db1, "CREATE VIEW dbo.V_Otro AS SELECT 1 AS x")
        fixture.Exec(db2, "CREATE PROCEDURE dbo.P_Uno AS SELECT 2")
    End Sub

    <ClassCleanup>
    Public Shared Sub ClassTeardown()
        If fixture IsNot Nothing Then fixture.Dispose()
    End Sub

    <TestInitialize>
    Public Sub Setup()
        If fixture Is Nothing Then Assert.Inconclusive("LocalDB no esta disponible en este equipo.")
        workFolder = NewTempFolder("RespaldoPruebas")
        sourceFolder = Path.Combine(workFolder, "src")
        Directory.CreateDirectory(Path.Combine(sourceFolder, "Interfaz"))
        ' a.sql en ANSI (Windows-1252) con ñ en el nombre; c.sql duplica P_Uno con otro cuerpo.
        File.WriteAllBytes(FileA, Encoding.GetEncoding(1252).GetBytes("CREATE PROCEDURE dbo.P_Año AS SELECT N'ñ'" & vbCrLf & "GO" & vbCrLf))
        File.WriteAllText(FileB, "ALTER PROCEDURE dbo.P_Uno AS SELECT 1" & vbCrLf & "GO" & vbCrLf, New UTF8Encoding(False))
        File.WriteAllText(FileC, "ALTER PROCEDURE [dbo].[P_Uno] AS SELECT 99" & vbCrLf & "GO" & vbCrLf, Encoding.Unicode)
        File.WriteAllText(FileD, "CREATE VIEW dbo.V_Otro AS SELECT 1 AS x" & vbCrLf & "GO" & vbCrLf, New UTF8Encoding(False))
    End Sub

    <TestCleanup>
    Public Sub Cleanup()
        TryDeleteFolder(workFolder)
    End Sub

    Private ReadOnly Property FileA As String
        Get
            Return Path.Combine(sourceFolder, "a.sql")
        End Get
    End Property
    Private ReadOnly Property FileB As String
        Get
            Return Path.Combine(sourceFolder, "b.sql")
        End Get
    End Property
    Private ReadOnly Property FileC As String
        Get
            Return Path.Combine(sourceFolder, "Interfaz", "c.sql")
        End Get
    End Property
    Private ReadOnly Property FileD As String
        Get
            Return Path.Combine(sourceFolder, "d.sql")
        End Get
    End Property

    Private Function Request(ParamArray databases As String()) As BackupRequest
        Return fixture.NewRequest(databases, sourceFolder, Path.Combine(workFolder, "out"))
    End Function

    Private Shared Function Row(analysis As BackupAnalysis, database As String, objectName As String) As AnalysisItem
        Return analysis.Items.Single(Function(x) x.DatabaseName = database AndAlso x.Declaration.ObjectName = objectName)
    End Function

    <TestMethod>
    Public Sub AnalizarUnaBase_ArchivoAnsiDuplicadosYCambios()
        Dim analysis As BackupAnalysis = BackupService.Analyze(Request(db1), CancellationToken.None, Nothing)
        Assert.AreEqual(3, analysis.Items.Count)
        Assert.IsTrue(analysis.Items.All(Function(x) x.CanBackup), String.Join(" | ", analysis.Items.Select(Function(x) x.Status)))

        Dim uno As AnalysisItem = Row(analysis, db1, "P_Uno")
        Assert.AreEqual(1, uno.AlsoDeclaredIn.Count)
        Assert.AreEqual(FileC, uno.AlsoDeclaredIn(0).SourceFile)
        StringAssert.Contains(uno.Detail, "c.sql")
        ' b.sql coincide con la base, pero c.sql tiene otro cuerpo.
        Assert.AreEqual("Iguales (varia por archivo)", uno.ChangeSummary)
        Assert.IsFalse(uno.HasChanges)
        Assert.AreEqual("Iguales", Row(analysis, db1, "P_Año").ChangeSummary)
    End Sub

    <TestMethod>
    Public Sub AnalizarVariasBases_UnaFilaPorObjetoYBase()
        Dim analysis As BackupAnalysis = BackupService.Analyze(Request(db1, db2), CancellationToken.None, Nothing)
        Assert.AreEqual(6, analysis.Items.Count)
        Assert.AreEqual(6, analysis.Items.Select(Function(x) x.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count())
        Dim uno2 As AnalysisItem = Row(analysis, db2, "P_Uno")
        Assert.IsTrue(uno2.CanBackup)
        StringAssert.Contains(uno2.Current.Definition, "SELECT 2")
        Assert.IsTrue(uno2.HasChanges)
        Assert.AreEqual("-1 +1 (varia por archivo)", uno2.ChangeSummary)
        Dim ano2 As AnalysisItem = Row(analysis, db2, "P_Año")
        Assert.AreEqual("No visible o inexistente", ano2.Status)
        Assert.IsNull(ano2.ChangeSummary)
    End Sub

    <TestMethod>
    Public Sub RespaldarVariasBases_UsaUnaSolaConexionParaVerificar()
        Dim analysis As BackupAnalysis = BackupService.Analyze(Request(db1, db2), CancellationToken.None, Nothing)
        Dim opensBefore As Integer = BackupService.ConnectionsOpened
        BackupService.Save(Request(db1, db2), analysis, analysis.Items.Where(Function(x) x.CanBackup).Select(Function(x) x.Key), CancellationToken.None, Nothing)
        Assert.AreEqual(1, BackupService.ConnectionsOpened - opensBefore)
    End Sub

    <TestMethod>
    Public Sub AnalizarBaseQueFalla_NoDetieneLasDemas()
        ' La base que falla va primero: la sesion debe recuperarse y seguir con la siguiente.
        Dim reversed As BackupAnalysis = BackupService.Analyze(Request("Base_Que_No_Existe_XYZ", db1), CancellationToken.None, Nothing)
        Assert.IsTrue(reversed.Items.Where(Function(x) x.DatabaseName = db1).All(Function(x) x.CanBackup))
        Dim analysis As BackupAnalysis = BackupService.Analyze(Request(db1, "Base_Que_No_Existe_XYZ"), CancellationToken.None, Nothing)
        Dim failed As List(Of AnalysisItem) = analysis.Items.Where(Function(x) x.DatabaseName = "Base_Que_No_Existe_XYZ").ToList()
        Assert.AreEqual(3, failed.Count)
        Assert.IsTrue(failed.All(Function(x) x.Status = "Base no disponible" AndAlso Not x.CanBackup))
        Assert.IsTrue(analysis.Items.Where(Function(x) x.DatabaseName = db1).All(Function(x) x.CanBackup))
    End Sub

    <TestMethod>
    Public Sub AnalizarTodasLasBasesFallan_InformaElError()
        Assert.Throws(Of SqlException)(Sub() BackupService.Analyze(Request("Base_Que_No_Existe_XYZ"), CancellationToken.None, Nothing))
    End Sub

    <TestMethod>
    Public Sub RespaldarUnaBase_EstructuraDeSiempreYScriptDeReversion()
        Dim analysis As BackupAnalysis = BackupService.Analyze(Request(db1), CancellationToken.None, Nothing)
        Dim result As BackupResult = BackupService.Save(Request(db1), analysis, analysis.Items.Select(Function(x) x.Key), CancellationToken.None, Nothing)
        Dim anoFile As String = Path.Combine(result.Folder, "Procedimientos", "[dbo].[P_Año]_" & db1 & ".sql")
        Assert.IsTrue(File.Exists(anoFile))
        Dim saved As String = File.ReadAllText(anoFile)
        StringAssert.StartsWith(saved, "USE [" & db1 & "]")
        StringAssert.Contains(saved, "N'ñ'")
        Assert.AreEqual(1, result.RestoreScripts.Count)
        Dim restore As String = File.ReadAllText(Path.Combine(result.Folder, result.RestoreScripts(0)))
        StringAssert.Contains(restore, "ALTER PROCEDURE dbo.P_Año")
        StringAssert.Contains(restore, "ALTER VIEW dbo.V_Otro")
        Assert.AreEqual(4, result.VerifiedFiles) ' 3 objetos + 1 script de reversion
        StringAssert.StartsWith(File.ReadAllText(Path.Combine(result.Folder, "Verificacion.txt")), "Base: " & db1)
    End Sub

    <TestMethod>
    Public Sub RespaldarVariasBases_SubcarpetaPorBaseConSuUse()
        Dim analysis As BackupAnalysis = BackupService.Analyze(Request(db1, db2), CancellationToken.None, Nothing)
        Dim keys As String() = {Row(analysis, db1, "P_Uno").Key, Row(analysis, db2, "P_Uno").Key, Row(analysis, db1, "P_Año").Key}
        Dim result As BackupResult = BackupService.Save(Request(db1, db2), analysis, keys, CancellationToken.None, Nothing)
        Dim file1 As String = File.ReadAllText(Path.Combine(result.Folder, db1, "Procedimientos", "[dbo].[P_Uno]_" & db1 & ".sql"))
        Dim file2 As String = File.ReadAllText(Path.Combine(result.Folder, db2, "Procedimientos", "[dbo].[P_Uno]_" & db2 & ".sql"))
        StringAssert.StartsWith(file1, "USE [" & db1 & "]")
        StringAssert.Contains(file1, "SELECT 1")
        StringAssert.StartsWith(file2, "USE [" & db2 & "]")
        StringAssert.Contains(file2, "SELECT 2")
        CollectionAssert.AreEquivalent({Path.Combine(db1, "Restaurar_" & db1 & ".sql"), Path.Combine(db2, "Restaurar_" & db2 & ".sql")}, result.RestoreScripts)
        StringAssert.StartsWith(File.ReadAllText(Path.Combine(result.Folder, "Verificacion.txt")), "Bases: " & String.Join(", ", {db1, db2}.OrderBy(Function(x) x, StringComparer.OrdinalIgnoreCase)))
        StringAssert.Contains(File.ReadAllText(Path.Combine(result.Folder, "Objetos_no_respaldados.txt")), "[" & db2 & "] Procedimiento [dbo].[P_Año]")
    End Sub

    ' Caso real: destino largo + subcarpeta por base + nombre de archivo largo superaban los 260 caracteres de Windows.
    <TestMethod>
    Public Sub RespaldarConRutasDeMasDe260Caracteres()
        Dim longName As String = "Destino_con_un_nombre_muy_largo_para_superar_el_limite_de_rutas_de_Windows_" & New String("x"c, 60)
        Dim destination As String = Path.Combine(workFolder, longName, "Gestor Tiempo Extra", "Fase 1")
        Dim analysis As BackupAnalysis = BackupService.Analyze(fixture.NewRequest({db1, db2}, sourceFolder, destination), CancellationToken.None, Nothing)
        Dim result As BackupResult = BackupService.Save(fixture.NewRequest({db1, db2}, sourceFolder, destination), analysis, analysis.Items.Where(Function(x) x.CanBackup).Select(Function(x) x.Key), CancellationToken.None, Nothing)
        Dim saved As String = Path.Combine(result.Folder, db1, "Procedimientos", "[dbo].[P_Año]_" & db1 & ".sql")
        Assert.IsTrue(saved.Length > 260, "La prueba debe superar el limite: " & saved.Length.ToString())
        Assert.IsTrue(File.Exists(BackupService.LongPath(saved)))
        StringAssert.Contains(File.ReadAllText(BackupService.LongPath(saved)), "N'ñ'")
        Assert.IsTrue(result.LongestPath > 260)
        ' No quedan carpetas temporales.
        Assert.AreEqual(0, Directory.GetDirectories(BackupService.LongPath(destination), ".*").Length)
    End Sub

    <TestMethod>
    Public Sub RespaldarVerificaSoloLosArchivosSeleccionados()
        Dim analysis As BackupAnalysis = BackupService.Analyze(Request(db1), CancellationToken.None, Nothing)
        File.AppendAllText(FileD, "-- cambio ajeno" & vbCrLf)
        ' d.sql (V_Otro) cambio, pero no esta seleccionado: el respaldo sigue.
        BackupService.Save(Request(db1), analysis, {Row(analysis, db1, "P_Año").Key, Row(analysis, db1, "P_Uno").Key}, CancellationToken.None, Nothing)
        Dim viewError As InvalidOperationException = Assert.Throws(Of InvalidOperationException)(Sub() BackupService.Save(Request(db1), analysis, {Row(analysis, db1, "V_Otro").Key}, CancellationToken.None, Nothing))
        StringAssert.Contains(viewError.Message, "d.sql")
        File.AppendAllText(FileC, "-- cambio en duplicado" & vbCrLf, Encoding.Unicode)
        Dim duplicateError As InvalidOperationException = Assert.Throws(Of InvalidOperationException)(Sub() BackupService.Save(Request(db1), analysis, {Row(analysis, db1, "P_Uno").Key}, CancellationToken.None, Nothing))
        StringAssert.Contains(duplicateError.Message, "c.sql")
    End Sub

    <TestMethod>
    Public Sub RespaldarDefinicionCambiadaEnElServidor_SeDetiene()
        fixture.Exec(db1, "CREATE PROCEDURE dbo.P_Cambia AS SELECT 1")
        Try
            File.WriteAllText(Path.Combine(sourceFolder, "cambia.sql"), "ALTER PROCEDURE dbo.P_Cambia AS SELECT 1")
            Dim analysis As BackupAnalysis = BackupService.Analyze(Request(db1), CancellationToken.None, Nothing)
            fixture.Exec(db1, "ALTER PROCEDURE dbo.P_Cambia AS SELECT 2")
            Dim failure As InvalidOperationException = Assert.Throws(Of InvalidOperationException)(Sub() BackupService.Save(Request(db1), analysis, {Row(analysis, db1, "P_Cambia").Key}, CancellationToken.None, Nothing))
            StringAssert.Contains(failure.Message, "cambio desde la vista previa")
        Finally
            fixture.Exec(db1, "DROP PROCEDURE dbo.P_Cambia")
        End Try
    End Sub

    <TestMethod>
    Public Sub BuscarEnBases_EncuentraCadaObjetoDondeExiste()
        Dim analysis As BackupAnalysis = BackupService.Analyze(Request(db1), CancellationToken.None, Nothing)
        Dim report As DatabaseSearchReport = BackupService.FindDatabases(Request(db1), {Row(analysis, db1, "P_Uno").Declaration, Row(analysis, db1, "P_Año").Declaration}, CancellationToken.None, Nothing)
        CollectionAssert.IsSubsetOf({db1, db2}, report.Locations.Where(Function(x) x.ObjectName = "P_Uno").Select(Function(x) x.DatabaseName).ToArray())
        CollectionAssert.AreEqual({db1}, report.Locations.Where(Function(x) x.ObjectName = "P_Año").Select(Function(x) x.DatabaseName).ToArray())
        Assert.AreEqual(0, report.SkippedDatabases.Count, String.Join("; ", report.SkippedDatabases))
    End Sub

    <TestMethod>
    Public Sub BuscarEnBases_RespetaLaCancelacion()
        Dim analysis As BackupAnalysis = BackupService.Analyze(Request(db1), CancellationToken.None, Nothing)
        Using cancelled As New CancellationTokenSource()
            cancelled.Cancel()
            Assert.Throws(Of OperationCanceledException)(Sub() BackupService.FindDatabases(Request(db1), {Row(analysis, db1, "P_Uno").Declaration}, cancelled.Token, Nothing))
        End Using
    End Sub

    <TestMethod>
    Public Sub ListarBases_SoloBasesDeUsuarioAccesibles()
        Dim names As List(Of String) = BackupService.ListDatabases(Request(), CancellationToken.None)
        CollectionAssert.IsSubsetOf({db1, db2}, names)
        Assert.IsFalse(names.Any(Function(x) {"master", "model", "msdb", "tempdb"}.Contains(x.ToLowerInvariant())))
        CollectionAssert.AreEqual(names.OrderBy(Function(x) x, StringComparer.OrdinalIgnoreCase).ToList(), names)
    End Sub

    <TestMethod>
    Public Sub AnalizarMilesDeObjetosEnVariasBases()
        Const count As Integer = 3000
        Dim creates As New StringBuilder()
        Dim declarations As New StringBuilder()
        For index As Integer = 1 To count
            creates.Append("CREATE PROCEDURE dbo.P_Masivo_").Append(index).Append(" AS SELECT ").Append(index).Append(vbCrLf).Append("GO").Append(vbCrLf)
            declarations.Append("ALTER PROCEDURE dbo.P_Masivo_").Append(index).Append(" AS SELECT ").Append(If(index Mod 10 = 0, -index, index)).Append(vbCrLf).Append("GO").Append(vbCrLf)
        Next
        Dim massFixture As LocalDbFixture = LocalDbFixture.TryCreate(2)
        Try
            For Each db As String In massFixture.Databases
                massFixture.ExecScript(db, creates.ToString())
            Next
            Dim massSource As String = Path.Combine(workFolder, "masivo")
            Directory.CreateDirectory(massSource)
            File.WriteAllText(Path.Combine(massSource, "masivo.sql"), declarations.ToString())
            Dim request As BackupRequest = massFixture.NewRequest(massFixture.Databases, massSource, Path.Combine(workFolder, "out_masivo"))

            Dim opensBefore As Integer = BackupService.ConnectionsOpened
            Dim watch As Stopwatch = Stopwatch.StartNew()
            Dim analysis As BackupAnalysis = BackupService.Analyze(request, CancellationToken.None, Nothing)
            watch.Stop()
            Console.WriteLine("Analizar " & count.ToString() & " objetos x 2 bases: " & watch.ElapsedMilliseconds.ToString() & " ms")
            Assert.AreEqual(1, BackupService.ConnectionsOpened - opensBefore, "Analizar varias bases debe usar una sola conexion")
            Assert.AreEqual(count * 2, analysis.Items.Count)
            Assert.IsTrue(analysis.Items.All(Function(x) x.CanBackup))
            Assert.AreEqual((count \ 10) * 2, analysis.Items.Where(Function(x) x.HasChanges).Count())
        Finally
            massFixture.Dispose()
        End Try
    End Sub

    <TestMethod>
    Public Sub ConexionUsaSqlCredentialYCifrado()
        Dim createConnection As MethodInfo = GetType(BackupService).GetMethod("CreateConnection", BindingFlags.Static Or BindingFlags.NonPublic)
        Using connection As SqlConnection = DirectCast(createConnection.Invoke(Nothing, New Object() {Request(db1), db1}), SqlConnection)
            Dim text As String = connection.ConnectionString.ToLowerInvariant()
            Assert.IsFalse(text.Contains("password"), connection.ConnectionString)
            Assert.IsFalse(text.Contains("user id"), connection.ConnectionString)
            Assert.AreEqual(fixture.Login, connection.Credential.UserId)
            StringAssert.Contains(connection.ConnectionString, "Encrypt=True")
            connection.Open()
            Using command As SqlCommand = connection.CreateCommand()
                command.CommandText = "SELECT SUSER_NAME()"
                Assert.AreEqual(fixture.Login, CStr(command.ExecuteScalar()))
            End Using
        End Using
    End Sub
End Class
