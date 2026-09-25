Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports System.Text
Imports System.Text.RegularExpressions
Imports Microsoft.VisualBasic

' Genera un unico script por base que devuelve los objetos respaldados a su definicion.
' Usa ALTER (conserva permisos, a diferencia de DROP + CREATE); si el objeto ya no existe,
' antes crea un esqueleto minimo con EXEC para que el ALTER funcione.
Friend NotInheritable Class RestoreScriptBuilder
    Private Shared ReadOnly LeadingKeyword As New Regex("^(?:CREATE\s+OR\s+ALTER|CREATE|ALTER)(?=\s)", RegexOptions.IgnoreCase)

    Private Sub New()
    End Sub

    Public Shared Function Build(databaseName As String, server As String, objects As IEnumerable(Of CatalogObject), createdAt As DateTime) As String
        Dim ordered As List(Of CatalogObject) = objects.OrderBy(Function(x) RestoreOrder(x.TypeCode)).
                                                       ThenBy(Function(x) x.SchemaName, StringComparer.OrdinalIgnoreCase).
                                                       ThenBy(Function(x) x.ObjectName, StringComparer.OrdinalIgnoreCase).ToList()
        Dim script As New StringBuilder()
        AppendLine(script, "-- Script de reversion generado por Respaldo objetos SQL TE")
        AppendLine(script, "-- Servidor: " & server & " | Base: " & databaseName & " | Respaldo: " & createdAt.ToString("yyyy-MM-dd HH:mm:ss"))
        AppendLine(script, "-- Devuelve " & ordered.Count.ToString() & " objeto(s) a la definicion respaldada.")
        AppendLine(script, "-- Los objetos existentes se modifican con ALTER, por lo que se conservan sus permisos.")
        AppendLine(script, "-- Si un objeto ya no existe, primero se crea un esqueleto minimo y luego se aplica el ALTER.")
        AppendLine(script, "-- Orden: funciones, vistas, procedimientos y triggers. Ejecutelo completo y revise los mensajes.")
        AppendLine(script, "")
        AppendLine(script, "USE " & QuoteName(databaseName))
        AppendLine(script, "GO")

        For Each entry As CatalogObject In ordered
            Dim label As String = If(String.IsNullOrEmpty(entry.SchemaName), QuoteName(entry.ObjectName), QuoteName(entry.SchemaName) & "." & QuoteName(entry.ObjectName))
            AppendLine(script, "")
            AppendLine(script, "-- ===== " & KindLabel(entry.TypeCode) & " " & label & " =====")
            AppendLine(script, "SET ANSI_NULLS " & If(entry.AnsiNulls, "ON", "OFF"))
            AppendLine(script, "GO")
            AppendLine(script, "SET QUOTED_IDENTIFIER " & If(entry.QuotedIdentifier, "ON", "OFF"))
            AppendLine(script, "GO")
            ' Los triggers no admiten un esqueleto generico (dependen de su tabla); si faltan se crean con su definicion completa.
            Dim stub As String = If(entry.TypeCode = "TR", entry.Definition, StubFor(entry.TypeCode, label))
            AppendLine(script, "IF " & MissingCondition(entry, label) & " EXEC(N'" & stub.Replace("'", "''") & "')")
            AppendLine(script, "GO")
            Dim alterText As String = ToAlter(entry.Definition)
            If alterText Is Nothing Then
                AppendLine(script, "-- ATENCION: no se encontro el CREATE en la definicion; se deja tal como la devolvio SQL Server.")
                alterText = entry.Definition
            End If
            AppendLine(script, NormalizeLines(alterText).TrimEnd())
            AppendLine(script, "GO")
        Next
        Return script.ToString()
    End Function

    ' Cambia solo la palabra CREATE de la declaracion por ALTER; conserva los comentarios previos y el resto del texto.
    Friend Shared Function ToAlter(definition As String) As String
        If definition Is Nothing Then Return Nothing
        Dim declarations As List(Of DeclaredObject) = SqlObjectParser.Parse(definition, Nothing)
        If declarations.Count = 0 Then Return Nothing
        Dim start As Integer = declarations(0).DeclarationStart
        Dim rest As String = definition.Substring(start)
        Dim match As Match = LeadingKeyword.Match(rest)
        If Not match.Success Then Return Nothing
        Return definition.Substring(0, start) & "ALTER" & rest.Substring(match.Length)
    End Function

    Private Shared Function MissingCondition(entry As CatalogObject, label As String) As String
        If String.IsNullOrEmpty(entry.SchemaName) Then
            ' Trigger de base de datos (DDL): no pertenece a un esquema, OBJECT_ID no lo encuentra.
            Return "NOT EXISTS (SELECT 1 FROM sys.triggers WHERE parent_class = 0 AND name = N'" & entry.ObjectName.Replace("'", "''") & "')"
        End If
        Return "OBJECT_ID(N'" & label.Replace("'", "''") & "', N'" & entry.TypeCode & "') IS NULL"
    End Function

    Private Shared Function StubFor(typeCode As String, label As String) As String
        Select Case typeCode
            Case "P" : Return "CREATE PROCEDURE " & label & " AS RETURN 0"
            Case "V" : Return "CREATE VIEW " & label & " AS SELECT 1 AS columna"
            Case "FN" : Return "CREATE FUNCTION " & label & "() RETURNS INT AS BEGIN RETURN 0 END"
            Case "IF" : Return "CREATE FUNCTION " & label & "() RETURNS TABLE AS RETURN SELECT 1 AS columna"
            Case "TF" : Return "CREATE FUNCTION " & label & "() RETURNS @t TABLE (columna INT) AS BEGIN RETURN END"
            Case Else : Throw New InvalidOperationException("Tipo de objeto no soportado para reversion: " & typeCode)
        End Select
    End Function

    Private Shared Function RestoreOrder(typeCode As String) As Integer
        Select Case typeCode
            Case "FN", "IF", "TF" : Return 0
            Case "V" : Return 1
            Case "P" : Return 2
            Case Else : Return 3
        End Select
    End Function

    Private Shared Function KindLabel(typeCode As String) As String
        Select Case typeCode
            Case "P" : Return "Procedimiento"
            Case "V" : Return "Vista"
            Case "TR" : Return "Trigger"
            Case Else : Return "Funcion"
        End Select
    End Function

    Private Shared Function QuoteName(value As String) As String
        Return "[" & value.Replace("]", "]]") & "]"
    End Function

    Private Shared Function NormalizeLines(value As String) As String
        Return value.Replace(vbCrLf, vbLf).Replace(vbCr, vbLf).Replace(vbLf, vbCrLf)
    End Function

    Private Shared Sub AppendLine(script As StringBuilder, line As String)
        script.Append(line).Append(vbCrLf)
    End Sub
End Class
