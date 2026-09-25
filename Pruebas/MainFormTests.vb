Imports System.Diagnostics
Imports System.IO
Imports System.Reflection
Imports System.Runtime.InteropServices
Imports System.Text
Imports System.Windows.Forms
Imports Microsoft.VisualStudio.TestTools.UnitTesting

' Pruebas de la ventana principal: se manejan los botones reales dentro de Application.Run, como en la aplicacion.
<TestClass>
Public Class MainFormTests
    Private ReadOnly failures As New List(Of String)()
    Private ReadOnly shownDialogs As New List(Of String)()
    ' Accion a ejecutar sobre el proximo dialogo de ese tipo (en lugar de cerrarlo).
    Private ReadOnly dialogActions As New Dictionary(Of Type, Action(Of Form))()
    Private Shared ReadOnly AutoClosedTitles As String() = {"Error de conexion", "Error de analisis", "Error de busqueda", "Error de respaldo", "Resultado", "Bases donde aparecen los objetos"}

    <DllImport("user32.dll", CharSet:=CharSet.Unicode)>
    Private Shared Function FindWindow(className As String, windowName As String) As IntPtr
    End Function

    <DllImport("user32.dll")>
    Private Shared Function PostMessage(hWnd As IntPtr, msg As Integer, wParam As IntPtr, lParam As IntPtr) As Boolean
    End Function

    Private Sub Check(name As String, ok As Boolean, Optional info As String = "")
        If Not ok Then failures.Add(name & If(String.IsNullOrEmpty(info), "", " -> " & info))
    End Sub

    Private Sub ClickAndWait(form As MainForm, button As Button, Optional cancelImmediately As Boolean = False)
        shownDialogs.Clear()
        button.PerformClick()
        If cancelImmediately Then GetField(Of Button)(form, "cancelOperationButton").PerformClick()
        Dim watch As Stopwatch = Stopwatch.StartNew()
        While (GetField(Of Boolean)(form, "busy") OrElse watch.ElapsedMilliseconds < 1500 OrElse dialogActions.Count > 0) AndAlso watch.ElapsedMilliseconds < 30000
            Application.DoEvents()
            Threading.Thread.Sleep(10)
        End While
        dialogActions.Clear()
    End Sub

    Private Sub CheckIdle(form As MainForm, label As String)
        Dim ok As Boolean = Not GetField(Of Boolean)(form, "busy") AndAlso
                            GetField(Of TableLayoutPanel)(form, "inputs").Enabled AndAlso
                            GetField(Of FlowLayoutPanel)(form, "optionsPanel").Enabled AndAlso
                            GetField(Of Button)(form, "analyzeButton").Enabled AndAlso
                            Not GetField(Of Button)(form, "cancelOperationButton").Enabled AndAlso
                            GetField(Of Object)(form, "operationCancellation") Is Nothing
        Check(label & ": ventana desbloqueada y Cancelar deshabilitado", ok)
    End Sub

    Private Shared Function FindButton(parent As Control, text As String) As Button
        For Each child As Control In parent.Controls
            Dim button As Button = TryCast(child, Button)
            If button IsNot Nothing AndAlso button.Text = text Then Return button
            Dim nested As Button = FindButton(child, text)
            If nested IsNot Nothing Then Return nested
        Next
        Return Nothing
    End Function

    ' Cierra MessageBox y dialogos (o ejecuta la accion registrada) para que la prueba no se quede esperando.
    Private Function StartDialogHandler() As Timer
        Dim handler As New Timer With {.Interval = 150}
        AddHandler handler.Tick,
            Sub()
                For Each open As Form In Application.OpenForms.Cast(Of Form)().ToList()
                    Dim action As Action(Of Form) = Nothing
                    If dialogActions.TryGetValue(open.GetType(), action) Then
                        dialogActions.Remove(open.GetType())
                        shownDialogs.Add(open.Text)
                        action(open)
                        Return
                    End If
                Next
                For Each title As String In AutoClosedTitles
                    Dim window As IntPtr = FindWindow(Nothing, title)
                    If window <> IntPtr.Zero Then
                        shownDialogs.Add(title)
                        PostMessage(window, &H10, IntPtr.Zero, IntPtr.Zero) ' WM_CLOSE
                    End If
                Next
            End Sub
        handler.Start()
        Return handler
    End Function

    <STATestMethod>
    Public Sub FlujoCompletoConVariasBases()
        Dim fixture As LocalDbFixture = LocalDbFixture.TryCreate(2)
        If fixture Is Nothing Then Assert.Inconclusive("LocalDB no esta disponible en este equipo.")
        Dim db1 As String = fixture.Databases(0)
        Dim db2 As String = fixture.Databases(1)
        Dim root As String = NewTempFolder("RespaldoUi")
        Dim dialogHandler As Timer = Nothing
        Try
            UseTemporarySettingsFile()
            fixture.Exec(db1, "CREATE PROCEDURE dbo.P_Uno AS SELECT 1")
            fixture.Exec(db1, "CREATE VIEW dbo.V_Otro AS SELECT 1 AS x")
            fixture.Exec(db2, "CREATE PROCEDURE dbo.P_Uno AS SELECT 2")
            Dim source As String = Path.Combine(root, "src")
            Directory.CreateDirectory(source)
            File.WriteAllText(Path.Combine(source, "b.sql"), "ALTER PROCEDURE dbo.P_Uno AS SELECT 1" & vbCrLf & "GO" & vbCrLf & "CREATE VIEW dbo.V_Otro AS SELECT 1 AS x" & vbCrLf & "GO" & vbCrLf)

            dialogHandler = StartDialogHandler()
            Using form As New MainForm()
                form.ShowInTaskbar = False
                form.Opacity = 0
                AddHandler form.Shown, Sub() RunScenario(form, fixture, db1, db2, source, root)
                Application.Run(form)
            End Using
        Finally
            If dialogHandler IsNot Nothing Then dialogHandler.Dispose()
            fixture.Dispose()
            TryDeleteFolder(root)
        End Try
        Assert.AreEqual(0, failures.Count, String.Join(Environment.NewLine, failures))
    End Sub

    Private Sub RunScenario(form As MainForm, fixture As LocalDbFixture, db1 As String, db2 As String, source As String, root As String)
        Try
            Dim output As TextBox = GetField(Of TextBox)(form, "outputBox")
            Dim grid As DataGridView = GetField(Of DataGridView)(form, "objectsGrid")
            Dim databases As TextBox = GetField(Of TextBox)(form, "databaseBox")
            Dim chooseDatabases As Button = GetField(Of Button)(form, "loadDatabasesButton")
            Dim analyze As Button = GetField(Of Button)(form, "analyzeButton")
            Dim bothDatabases As String = String.Join(", ", {db1, db2}.OrderBy(Function(x) x, StringComparer.OrdinalIgnoreCase))
            Dim firstLine As Func(Of String) = Function() output.Text.Split({vbCr, vbLf}, StringSplitOptions.RemoveEmptyEntries)(0)

            ' Elegir bases: sin password solo pide servidor, usuario y password.
            GetField(Of TextBox)(form, "serverBox").Text = LocalDbServer
            GetField(Of TextBox)(form, "userBox").Text = fixture.Login
            databases.Text = ""
            ClickAndWait(form, chooseDatabases)
            Check("Elegir bases sin password pide servidor, usuario y password", shownDialogs.Contains("Error de conexion") AndAlso output.Text.EndsWith("Completa servidor, usuario y password."), output.Text)

            ' Dialogo de bases: filtro, marcar visibles y marcar otra.
            GetField(Of TextBox)(form, "passwordBox").Text = fixture.PasswordForUi
            Dim listed As New List(Of String)()
            Dim filtered As Integer = -1
            Dim okWithNone As Boolean = True
            dialogActions(GetType(DatabasePickerForm)) =
                Sub(dialog)
                    Dim list As CheckedListBox = GetField(Of CheckedListBox)(dialog, "databaseList")
                    listed.AddRange(list.Items.Cast(Of Object)().Select(Function(x) CStr(x)))
                    okWithNone = GetField(Of Button)(dialog, "okButton").Enabled
                    GetField(Of TextBox)(dialog, "filterBox").Text = db2
                    filtered = list.Items.Count
                    FindButton(dialog, "Marcar visibles").PerformClick()
                    GetField(Of TextBox)(dialog, "filterBox").Text = ""
                    list.SetItemChecked(list.Items.IndexOf(db1), True)
                    GetField(Of Button)(dialog, "okButton").PerformClick()
                End Sub
            ClickAndWait(form, chooseDatabases)
            Check("La lista trae las dos bases del login", listed.Contains(db1) AndAlso listed.Contains(db2), String.Join(", ", listed))
            Check("La lista excluye las bases del sistema", Not listed.Any(Function(x) {"master", "model", "msdb", "tempdb"}.Contains(x.ToLowerInvariant())))
            Check("Aceptar deshabilitado sin bases marcadas", Not okWithNone)
            Check("El filtro deja solo la base buscada", filtered = 1, filtered.ToString())
            Check("Quedan elegidas las dos bases", databases.Text = bothDatabases, databases.Text)
            CheckIdle(form, "Elegir bases")

            dialogActions(GetType(DatabasePickerForm)) = Sub(dialog) dialog.Close()
            ClickAndWait(form, chooseDatabases)
            Check("Cancelar el dialogo no cambia la seleccion", databases.Text = bothDatabases AndAlso output.Text.Contains("No se cambio la seleccion"), output.Text)

            GetField(Of TextBox)(form, "sourceBox").Text = source
            GetField(Of TextBox)(form, "destinationBox").Text = Path.Combine(root, "out")

            ' Validacion al analizar.
            GetField(Of TextBox)(form, "passwordBox").Text = ""
            ClickAndWait(form, analyze)
            Check("Analizar sin password muestra error", shownDialogs.Contains("Error de analisis") AndAlso output.Text.StartsWith("No se pudo analizar: Completa"), output.Text)
            CheckIdle(form, "Sin password")

            ' Analizar dos bases, con la columna Cambios.
            GetField(Of TextBox)(form, "passwordBox").Text = fixture.PasswordForUi
            ClickAndWait(form, analyze)
            Dim summary As String = GetField(Of Label)(form, "summaryLabel").Text
            Check("4 filas: 2 objetos x 2 bases", grid.Rows.Count = 4, output.Text)
            Check("Resumen con bases y cambios", summary.StartsWith("4 filas (objeto por base) en 2 base(s); 3 listas (1 con cambios)"), summary)
            Dim changes As Dictionary(Of String, String) = grid.Rows.Cast(Of DataGridViewRow)().ToDictionary(Function(r) CStr(r.Cells(1).Value) & "|" & CStr(r.Cells(3).Value), Function(r) CStr(r.Cells(6).Value))
            Check("Cambios: P_Uno igual en la base 1", changes(db1 & "|Procedimiento [dbo].[P_Uno]") = "Iguales", changes(db1 & "|Procedimiento [dbo].[P_Uno]"))
            Check("Cambios: P_Uno distinto en la base 2", changes(db2 & "|Procedimiento [dbo].[P_Uno]") = "-1 +1", changes(db2 & "|Procedimiento [dbo].[P_Uno]"))
            Check("Cambios: vacio si no hay definicion", changes(db2 & "|Vista [dbo].[V_Otro]") = "", changes(db2 & "|Vista [dbo].[V_Otro]"))
            CheckIdle(form, "Analizar")

            ' Aceptar las mismas bases (otro orden y mayusculas) conserva la vista previa.
            databases.Text = db2.ToLowerInvariant() & "; " & db1.ToUpperInvariant()
            ClickAndWait(form, analyze)
            dialogActions(GetType(DatabasePickerForm)) = Sub(dialog) GetField(Of Button)(dialog, "okButton").PerformClick()
            ClickAndWait(form, chooseDatabases)
            Check("Aceptar sin cambios conserva la vista previa", grid.Rows.Count = 4 AndAlso GetField(Of Object)(form, "analysis") IsNot Nothing, output.Text)

            ' Respaldar las filas listas de las dos bases.
            FindButton(form, "Marcar listos").PerformClick()
            ClickAndWait(form, GetField(Of Button)(form, "backupButton"))
            Check("Respaldar muestra el resultado", shownDialogs.Contains("Resultado") AndAlso output.Text.StartsWith("Respaldo creado:"), firstLine())
            Dim folders As String() = Directory.GetDirectories(Path.Combine(root, "out"), "Respaldo_Objetos_*")
            Check("Una subcarpeta por base", folders.Length = 1 AndAlso Directory.Exists(Path.Combine(folders(0), db1, "Procedimientos")) AndAlso Directory.Exists(Path.Combine(folders(0), db2, "Procedimientos")))
            Check("Un script de reversion por base", folders.Length = 1 AndAlso File.Exists(Path.Combine(folders(0), db1, "Restaurar_" & db1 & ".sql")) AndAlso File.Exists(Path.Combine(folders(0), db2, "Restaurar_" & db2 & ".sql")))
            ' Las bases se escribieron con otras mayusculas a proposito; SQL Server no distingue mayusculas en los nombres de base.
            Check("El resultado menciona los scripts de reversion", output.Text.Contains("Script(s) de reversion: ") AndAlso output.Text.IndexOf("Restaurar_" & db1 & ".sql", StringComparison.OrdinalIgnoreCase) >= 0, output.Text)
            Check("3 objetos guardados", output.Text.Contains("Objetos guardados: 3"), output.Text)
            CheckIdle(form, "Respaldar")

            ' Buscar en bases y agregar las encontradas.
            databases.Text = db1
            ClickAndWait(form, analyze)
            grid.ClearSelection()
            grid.Rows.Cast(Of DataGridViewRow)().First(Function(r) CStr(r.Cells(3).Value).Contains("P_Uno")).Selected = True
            dialogActions(GetType(DatabaseSearchForm)) =
                Sub(dialog)
                    GetField(Of DataGridView)(dialog, "locationsGrid").SelectAll()
                    GetField(Of Button)(dialog, "chooseButton").PerformClick()
                End Sub
            ClickAndWait(form, GetField(Of Button)(form, "searchDatabasesButton"))
            Check("Buscar agrega las bases encontradas", ParseSet(databases.Text).SetEquals({db1, db2}), databases.Text)
            CheckIdle(form, "Buscar")

            ' Cancelar un analisis largo conserva la vista previa anterior.
            ClickAndWait(form, analyze)
            Dim rowsBefore As Integer = grid.Rows.Count
            For index As Integer = 1 To 3000
                File.WriteAllText(Path.Combine(source, "p" & index.ToString() & ".sql"), "CREATE PROCEDURE dbo.P_Extra_" & index.ToString() & " AS SELECT 1" & vbCrLf & "GO" & vbCrLf)
            Next
            ClickAndWait(form, analyze, cancelImmediately:=True)
            Check("Cancelar muestra el mensaje", output.Text.StartsWith("Analisis cancelado"), output.Text)
            Check("Cancelar conserva la vista previa", grid.Rows.Count = rowsBefore, rowsBefore.ToString() & " -> " & grid.Rows.Count.ToString())
            CheckIdle(form, "Cancelar")

            ' Si la unica base falla se informa el error y se limpia la vista previa.
            databases.Text = "Base_Que_No_Existe_XYZ"
            ClickAndWait(form, analyze)
            Check("Base inexistente muestra error", shownDialogs.Contains("Error de analisis") AndAlso output.Text.StartsWith("No se pudo analizar:"), firstLine())
            Check("Base inexistente limpia la vista previa", grid.Rows.Count = 0 AndAlso GetField(Of Object)(form, "analysis") Is Nothing)
            CheckIdle(form, "Base inexistente")

            ' Servidor inexistente.
            GetField(Of TextBox)(form, "serverBox").Text = "127.0.0.1,1"
            ClickAndWait(form, chooseDatabases)
            Check("Servidor inexistente muestra error de conexion", shownDialogs.Contains("Error de conexion") AndAlso output.Text.StartsWith("No se pudieron obtener las bases:"), firstLine())
            CheckIdle(form, "Servidor inexistente")
        Catch ex As Exception
            failures.Add("Excepcion inesperada: " & ex.ToString())
        Finally
            form.Close()
        End Try
    End Sub

    Private Shared Function ParseSet(text As String) As HashSet(Of String)
        Return New HashSet(Of String)(MainForm.ParseDatabaseList(text), StringComparer.OrdinalIgnoreCase)
    End Function

    <STATestMethod>
    Public Sub ConfiguracionSeGuardaAlCerrarSinPassword()
        Dim settingsPath As String = UseTemporarySettingsFile()
        Try
            Using form As New MainForm()
                Check("Primer uso: destino en Documentos", GetField(Of TextBox)(form, "destinationBox").Text.EndsWith("Respaldos_TE"))
                Check("Primer uso: sin carpeta de scripts fija", GetField(Of TextBox)(form, "sourceBox").Text = "")
                form.ShowInTaskbar = False
                form.Opacity = 0
                form.Show()
                GetField(Of TextBox)(form, "serverBox").Text = "MI_SERVIDOR"
                GetField(Of TextBox)(form, "userBox").Text = "mi_usuario"
                GetField(Of TextBox)(form, "databaseBox").Text = "Base1, Base2"
                GetField(Of TextBox)(form, "passwordBox").Text = "SECRETO_NO_GUARDAR"
                GetField(Of CheckBox)(form, "encryptBox").Checked = False
                form.Close()
            End Using
            Dim xml As String = File.ReadAllText(settingsPath)
            Check("Se guarda al cerrar", xml.Contains("MI_SERVIDOR") AndAlso xml.Contains("Base1, Base2"))
            Check("El password no se guarda", Not xml.Contains("SECRETO_NO_GUARDAR"))
            Using reopened As New MainForm()
                Check("Se recuerda al reabrir", GetField(Of TextBox)(reopened, "serverBox").Text = "MI_SERVIDOR" AndAlso Not GetField(Of CheckBox)(reopened, "encryptBox").Checked)
                Check("Confiar en certificado deshabilitado si no se cifra", Not GetField(Of CheckBox)(reopened, "trustCertificateBox").Enabled)
            End Using
        Finally
            Try : File.Delete(settingsPath) : Catch ex As IOException : End Try
        End Try
        Assert.AreEqual(0, failures.Count, String.Join(Environment.NewLine, failures))
    End Sub

    <STATestMethod>
    Public Sub GrillaConMilesDeFilas()
        UseTemporarySettingsFile()
        Dim items As New List(Of AnalysisItem)()
        For index As Integer = 1 To 3000
            items.Add(New AnalysisItem With {
                .Key = index.ToString(), .DatabaseName = "Base", .Status = "Listo", .Detail = "d", .ChangeSummary = "-1 +1", .HasChanges = True,
                .Declaration = New DeclaredObject With {.Kind = "Procedimiento", .SchemaName = "dbo", .ObjectName = "P_" & index.ToString(), .SourceFile = "x.sql"},
                .Current = New CatalogObject With {.Definition = "x"},
                .AlsoDeclaredIn = New List(Of DeclaredObject)()
            })
        Next
        items(0).Status = "Ambiguo"
        Dim flags As BindingFlags = BindingFlags.Instance Or BindingFlags.NonPublic
        Using form As New MainForm()
            form.ShowInTaskbar = False
            form.Opacity = 0
            form.Show()
            GetType(MainForm).GetField("analysis", flags).SetValue(form, New BackupAnalysis With {.Items = items})
            Dim grid As DataGridView = GetField(Of DataGridView)(form, "objectsGrid")
            Dim watch As Stopwatch = Stopwatch.StartNew()
            GetType(MainForm).GetMethod("PopulateGrid", flags).Invoke(form, New Object() {items})
            Dim fill As Long = watch.ElapsedMilliseconds
            GetType(MainForm).GetMethod("SetAllSelection", flags).Invoke(form, New Object() {False})
            Dim unmark As Long = watch.ElapsedMilliseconds - fill
            GetType(MainForm).GetMethod("SetAllSelection", flags).Invoke(form, New Object() {True})
            watch.Stop()
            Console.WriteLine("3000 filas: llenar " & fill.ToString() & " ms, desmarcar " & unmark.ToString() & " ms, marcar " & (watch.ElapsedMilliseconds - fill - unmark).ToString() & " ms")
            Assert.AreEqual(3000, grid.Rows.Count)
            Assert.IsTrue(grid.Rows(0).Cells(0).ReadOnly AndAlso Not CBool(grid.Rows(0).Cells(0).Value), "La fila no lista debe quedar de solo lectura")
            Assert.AreEqual(2999, grid.Rows.Cast(Of DataGridViewRow)().Count(Function(r) CBool(r.Cells(0).Value)))
            Assert.IsTrue(GetField(Of Button)(form, "backupButton").Enabled)
            ' Limite amplio: el tiempo varia mucho segun la carga del equipo. Detecta regresiones cuadraticas (minutos), no micro variaciones.
            Assert.IsTrue(watch.ElapsedMilliseconds < 30000, "Llenar y marcar 3000 filas tardo " & watch.ElapsedMilliseconds.ToString() & " ms")
            form.Close()
        End Using
    End Sub
End Class
