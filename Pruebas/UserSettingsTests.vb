Imports System.IO
Imports Microsoft.VisualStudio.TestTools.UnitTesting

<TestClass>
Public Class UserSettingsTests
    Private folder As String

    <TestInitialize>
    Public Sub Setup()
        folder = NewTempFolder("RespaldoSettings")
    End Sub

    <TestCleanup>
    Public Sub Cleanup()
        TryDeleteFolder(folder)
    End Sub

    <TestMethod>
    Public Sub GuardarYLeerConservaLosValores()
        Dim settingsFile As String = Path.Combine(folder, "sub", "configuracion.xml")
        Dim settings As New UserSettings With {.Server = "SRV\INST", .Database = "Base_Ñandú, Otra", .UserName = "usr", .SourceFolder = "C:\a b\c", .DestinationFolder = "D:\x", .IncludeSubfolders = True, .Encrypt = False, .TrustServerCertificate = False}
        settings.Save(settingsFile)
        settings.Server = "SRV2"
        settings.Save(settingsFile) ' sobrescribe con File.Replace
        Dim loaded As UserSettings = UserSettings.Load(settingsFile)
        Assert.AreEqual("SRV2", loaded.Server)
        Assert.AreEqual("Base_Ñandú, Otra", loaded.Database)
        Assert.AreEqual("usr", loaded.UserName)
        Assert.AreEqual("C:\a b\c", loaded.SourceFolder)
        Assert.IsTrue(loaded.IncludeSubfolders)
        Assert.IsFalse(loaded.Encrypt)
        Assert.IsFalse(File.Exists(settingsFile & ".tmp"))
    End Sub

    <TestMethod>
    Public Sub SinArchivoUsaValoresPorDefecto()
        Dim loaded As UserSettings = UserSettings.Load(Path.Combine(folder, "no_existe.xml"))
        Assert.IsNull(loaded.Server)
        Assert.IsTrue(loaded.Encrypt)
        Assert.IsTrue(loaded.TrustServerCertificate)
    End Sub

    <TestMethod>
    Public Sub ArchivoDanadoNoFalla()
        Dim bad As String = Path.Combine(folder, "malo.xml")
        File.WriteAllText(bad, "<esto no es xml")
        Assert.IsTrue(UserSettings.Load(bad).Encrypt)
    End Sub

    <TestMethod>
    Public Sub ListaDeBasesSeSeparaPorComaOPuntoYComa()
        CollectionAssert.AreEqual({"A", "b", "C"}, MainForm.ParseDatabaseList(" A, b ;C;; a ,").ToArray())
    End Sub
End Class
