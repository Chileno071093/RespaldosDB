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
    Public Sub DropEnSusVariantes()
        Dim script As String = "IF OBJECT_ID('dbo.P_C') IS NOT NULL DROP PROCEDURE dbo.P_C" & vbCrLf & "GO" & vbCrLf &
                               "DROP PROCEDURE IF EXISTS dbo.P_A, [Rep].[P_B]" & vbCrLf & "GO" & vbCrLf &
                               "DROP VIEW V_D; DROP FUNCTION dbo.F_E" & vbCrLf & "GO" & vbCrLf &
                               "DROP TABLE dbo.NoEsObjetoSoportado"
        Dim found As List(Of DeclaredObject) = SqlObjectParser.Parse(script, "x.sql")
        Assert.IsTrue(found.All(Function(x) x.IsDrop))
        CollectionAssert.AreEqual({"P_C", "P_A", "P_B", "V_D", "F_E"}, found.Select(Function(x) x.ObjectName).ToArray())
        CollectionAssert.AreEqual({"dbo", "dbo", "Rep", Nothing, "dbo"}, found.Select(Function(x) x.SchemaName).ToArray())
        CollectionAssert.AreEqual({"Procedimiento", "Procedimiento", "Procedimiento", "Vista", "Funcion"}, found.Select(Function(x) x.Kind).ToArray())
        Assert.AreEqual("DROP PROCEDURE dbo.P_C", found(0).CandidateText)
    End Sub

    <TestMethod>
    Public Sub LoQueEstaDentroDeUnObjetoNoCuenta()
        Dim script As String = "CREATE PROCEDURE dbo.P_Limpia AS" & vbCrLf &
                               "BEGIN" & vbCrLf &
                               "  DROP PROCEDURE dbo.P_Temporal" & vbCrLf &
                               "  CREATE TABLE #t (id INT)" & vbCrLf &
                               "  INSERT INTO dbo.Bitacora VALUES (1)" & vbCrLf &
                               "  GRANT EXECUTE ON dbo.X TO public" & vbCrLf &
                               "END" & vbCrLf & "GO" & vbCrLf &
                               "INSERT INTO dbo.Bitacora VALUES (2)"
        Dim unsupported As New List(Of UnsupportedStatement)()
        Dim found As List(Of DeclaredObject) = SqlObjectParser.Parse(script, "x.sql", unsupported)
        Assert.AreEqual(1, found.Count)
        Assert.IsFalse(found(0).IsDrop)
        ' Solo el INSERT de fuera del procedimiento.
        Assert.AreEqual(1, unsupported.Count)
        Assert.AreEqual(9, unsupported(0).Line)
        Assert.AreEqual("Datos", unsupported(0).Category)
    End Sub

    <TestMethod>
    Public Sub TriggersDeServidorYDeBaseYSinonimos()
        Dim script As String = "CREATE TRIGGER TRS ON ALL SERVER FOR CREATE_LOGIN AS PRINT 1" & vbCrLf & "GO" & vbCrLf &
                               "CREATE TRIGGER TRD ON DATABASE FOR CREATE_TABLE AS PRINT 1" & vbCrLf & "GO" & vbCrLf &
                               "DROP TRIGGER TRS2, TRS3 ON ALL SERVER" & vbCrLf & "GO" & vbCrLf &
                               "CREATE SYNONYM dbo.S_T FOR OtraBase.dbo.Tabla" & vbCrLf & "GO"
        Dim found As List(Of DeclaredObject) = SqlObjectParser.Parse(script, "x.sql")
        CollectionAssert.AreEqual({True, False, True, True, False}, found.Select(Function(x) x.IsServerScoped).ToArray())
        CollectionAssert.AreEqual({"TRS", "TRD", "TRS2", "TRS3", "S_T"}, found.Select(Function(x) x.ObjectName).ToArray())
        Assert.AreEqual("Sinonimo", found(4).Kind)
        Assert.AreEqual("dbo", found(4).SchemaName)
    End Sub

    <TestMethod>
    Public Sub SentenciasSinReversion()
        Dim script As String = "CREATE TABLE dbo.Pedido (id INT," & vbCrLf &
                               "  cliente INT REFERENCES dbo.Cliente(id) ON DELETE CASCADE ON UPDATE NO ACTION)" & vbCrLf &
                               "GO" & vbCrLf &
                               "ALTER TABLE dbo.Pedido ADD fecha DATE" & vbCrLf &
                               "CREATE NONCLUSTERED INDEX IX_Pedido ON dbo.Pedido (fecha)" & vbCrLf &
                               "CREATE TABLE #trabajo (id INT)" & vbCrLf &
                               "UPDATE dbo.Parametro SET valor = 1" & vbCrLf &
                               "GRANT EXECUTE ON dbo.P TO rol_app" & vbCrLf &
                               "CREATE USER app FOR LOGIN app" & vbCrLf &
                               "EXEC sys.sp_rename 'dbo.Pedido.fecha', 'fecha_alta', 'COLUMN'" & vbCrLf &
                               "DISABLE TRIGGER dbo.TR_X ON dbo.Pedido"
        Dim unsupported As New List(Of UnsupportedStatement)()
        SqlObjectParser.Parse(script, "lib.sql", unsupported)
        Dim summary As String() = unsupported.Select(Function(x) x.Line.ToString() & ":" & x.Category).ToArray()
        ' Linea 2 (ON DELETE / ON UPDATE) y la tabla temporal (linea 6) no cuentan.
        CollectionAssert.AreEqual({"1:Estructura", "4:Estructura", "5:Estructura", "7:Datos", "8:Permisos", "9:Permisos", "10:Otro", "11:Otro"}, summary, String.Join(", ", summary))
        Assert.AreEqual("ALTER TABLE dbo.Pedido ADD fecha DATE", unsupported(1).Text)
        Assert.AreEqual("lib.sql:7 | Datos | UPDATE dbo.Parametro SET valor = 1", unsupported(3).ToString())
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
