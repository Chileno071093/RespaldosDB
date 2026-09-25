Imports Microsoft.VisualStudio.TestTools.UnitTesting

<TestClass>
Public Class RestoreScriptBuilderTests
    <TestMethod>
    <DataRow("CREATE PROCEDURE dbo.P AS SELECT 1", "ALTER PROCEDURE dbo.P AS SELECT 1")>
    <DataRow("CREATE   PROCEDURE dbo.P AS SELECT 1", "ALTER   PROCEDURE dbo.P AS SELECT 1")>
    <DataRow("create view dbo.V AS SELECT 1 AS x", "ALTER view dbo.V AS SELECT 1 AS x")>
    <DataRow("-- comentario CREATE" & vbCrLf & "CREATE FUNCTION dbo.F() RETURNS INT AS BEGIN RETURN 1 END", "-- comentario CREATE" & vbCrLf & "ALTER FUNCTION dbo.F() RETURNS INT AS BEGIN RETURN 1 END")>
    Public Sub ToAlterCambiaSoloLaPalabraDeLaDeclaracion(definition As String, expected As String)
        Assert.AreEqual(expected, RestoreScriptBuilder.ToAlter(definition))
    End Sub

    <TestMethod>
    Public Sub SinonimosYTriggersDeServidor()
        Dim synonym As New CatalogObject With {.SchemaName = "dbo", .ObjectName = "S_T", .TypeCode = "SN", .Definition = "CREATE SYNONYM [dbo].[S_T] FOR [dbo].[T]", .AnsiNulls = True, .QuotedIdentifier = True}
        Dim procedure As New CatalogObject With {.SchemaName = "dbo", .ObjectName = "P", .TypeCode = "P", .Definition = "CREATE PROCEDURE dbo.P AS SELECT 1", .AnsiNulls = True, .QuotedIdentifier = True}
        Dim database As String = RestoreScriptBuilder.Build("Base", "SRV", {procedure, synonym}, DateTime.Now, 3)
        StringAssert.Contains(database, "IF OBJECT_ID(N'[dbo].[S_T]', N'SN') IS NOT NULL DROP SYNONYM [dbo].[S_T]")
        Assert.IsTrue(database.IndexOf("Sinonimo", StringComparison.Ordinal) < database.IndexOf("Procedimiento [dbo].[P]", StringComparison.Ordinal), "Los sinonimos van primero")
        StringAssert.Contains(database, "3 sentencia(s) que este script NO deshace")

        Dim serverTrigger As New CatalogObject With {.ObjectName = "TRS", .TypeCode = "TR", .IsServerScoped = True, .Definition = "CREATE TRIGGER TRS ON ALL SERVER FOR CREATE_LOGIN AS PRINT 1", .AnsiNulls = True, .QuotedIdentifier = True}
        Dim server As String = RestoreScriptBuilder.Build(BackupService.ServerScope, "SRV", {serverTrigger}, DateTime.Now)
        StringAssert.Contains(server, "USE [master]")
        StringAssert.Contains(server, "IF NOT EXISTS (SELECT 1 FROM sys.server_triggers WHERE name = N'TRS')")
        StringAssert.Contains(server, "ALTER TRIGGER TRS ON ALL SERVER")
        Assert.IsFalse(server.Contains("NO deshace"))
    End Sub

    <TestMethod>
    Public Sub ToAlterSinDeclaracionDevuelveNothing()
        Assert.IsNull(RestoreScriptBuilder.ToAlter("SELECT 1"))
    End Sub

    <TestMethod>
    Public Sub ScriptTieneUseOrdenYEsqueletos()
        Dim objects As New List(Of CatalogObject) From {
            New CatalogObject With {.SchemaName = "dbo", .ObjectName = "P_O'Neil", .TypeCode = "P", .Definition = "CREATE PROCEDURE dbo.[P_O'Neil] AS SELECT 'a'", .AnsiNulls = True, .QuotedIdentifier = True},
            New CatalogObject With {.SchemaName = "dbo", .ObjectName = "F", .TypeCode = "FN", .Definition = "CREATE FUNCTION dbo.F() RETURNS INT AS BEGIN RETURN 1 END", .AnsiNulls = False, .QuotedIdentifier = True},
            New CatalogObject With {.SchemaName = Nothing, .ObjectName = "TRD", .TypeCode = "TR", .Definition = "CREATE TRIGGER TRD ON DATABASE FOR CREATE_TABLE AS PRINT 'x'", .AnsiNulls = True, .QuotedIdentifier = True}
        }
        Dim script As String = RestoreScriptBuilder.Build("Mi]Base", "SRV", objects, New DateTime(2026, 9, 25, 10, 0, 0))
        StringAssert.Contains(script, "USE [Mi]]Base]")
        ' Funciones antes que procedimientos y triggers al final.
        Assert.IsTrue(script.IndexOf("Funcion [dbo].[F]", StringComparison.Ordinal) < script.IndexOf("Procedimiento [dbo].[P_O'Neil]", StringComparison.Ordinal))
        Assert.IsTrue(script.IndexOf("Procedimiento", StringComparison.Ordinal) < script.IndexOf("Trigger [TRD]", StringComparison.Ordinal))
        ' Comillas escapadas dentro de EXEC(N'...') y del OBJECT_ID.
        StringAssert.Contains(script, "IF OBJECT_ID(N'[dbo].[P_O''Neil]', N'P') IS NULL EXEC(N'CREATE PROCEDURE [dbo].[P_O''Neil] AS RETURN 0')")
        StringAssert.Contains(script, "ALTER PROCEDURE dbo.[P_O'Neil] AS SELECT 'a'")
        StringAssert.Contains(script, "SET ANSI_NULLS OFF")
        ' Trigger de base: se busca en sys.triggers y, si falta, se crea con su definicion completa.
        StringAssert.Contains(script, "IF NOT EXISTS (SELECT 1 FROM sys.triggers WHERE parent_class = 0 AND name = N'TRD') EXEC(N'CREATE TRIGGER TRD ON DATABASE FOR CREATE_TABLE AS PRINT ''x''')")
        StringAssert.Contains(script, "ALTER TRIGGER TRD ON DATABASE")
    End Sub
End Class
