Imports System
Imports System.Collections.Generic
Imports System.Drawing
Imports System.IO
Imports System.Linq
Imports System.Security
Imports System.Text
Imports System.Threading
Imports System.Threading.Tasks
Imports System.Windows.Forms
Imports Microsoft.VisualBasic

Friend NotInheritable Class MainForm
    Inherits Form

    Private ReadOnly serverBox As New TextBox()
    Private ReadOnly databaseBox As New TextBox()
    Private ReadOnly loadDatabasesButton As New Button()
    Private ReadOnly userBox As New TextBox()
    Private ReadOnly passwordBox As New TextBox()
    Private ReadOnly sourceBox As New TextBox()
    Private ReadOnly destinationBox As New TextBox()
    Private ReadOnly includeSubfoldersBox As New CheckBox()
    Private ReadOnly encryptBox As New CheckBox()
    Private ReadOnly trustCertificateBox As New CheckBox()
    Private ReadOnly optionsPanel As New FlowLayoutPanel()
    Private ReadOnly analyzeButton As New Button()
    Private ReadOnly backupButton As New Button()
    Private ReadOnly compareButton As New Button()
    Private ReadOnly searchDatabasesButton As New Button()
    Private ReadOnly cancelOperationButton As New Button()
    Private ReadOnly selectAllButton As New Button()
    Private ReadOnly selectNoneButton As New Button()
    Private ReadOnly objectsGrid As New DataGridView()
    Private ReadOnly summaryLabel As New Label()
    Private ReadOnly outputBox As New TextBox()
    Private ReadOnly inputs As New TableLayoutPanel()
    Private analysis As BackupAnalysis
    Private busy As Boolean
    Private suppressRefresh As Boolean
    Private operationCancellation As CancellationTokenSource

    Public Sub New()
        Me.Text = "Respaldo de objetos SQL"
        Me.StartPosition = FormStartPosition.CenterScreen
        Me.MinimumSize = New System.Drawing.Size(900, 620)
        Me.Size = New System.Drawing.Size(1150, 760)

        passwordBox.UseSystemPasswordChar = True

        Dim root As New TableLayoutPanel With {
            .Dock = DockStyle.Fill,
            .Padding = New Padding(16),
            .ColumnCount = 1,
            .RowCount = 6
        }
        root.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100.0F))
        root.RowStyles.Add(New RowStyle(SizeType.Absolute, 270.0F))
        root.RowStyles.Add(New RowStyle(SizeType.Absolute, 34.0F))
        root.RowStyles.Add(New RowStyle(SizeType.Absolute, 50.0F))
        root.RowStyles.Add(New RowStyle(SizeType.Absolute, 32.0F))
        root.RowStyles.Add(New RowStyle(SizeType.Percent, 100.0F))
        root.RowStyles.Add(New RowStyle(SizeType.Absolute, 110.0F))

        inputs.Dock = DockStyle.Fill
        inputs.ColumnCount = 3
        inputs.RowCount = 6
        inputs.ColumnStyles.Add(New ColumnStyle(SizeType.Absolute, 145.0F))
        inputs.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100.0F))
        inputs.ColumnStyles.Add(New ColumnStyle(SizeType.Absolute, 110.0F))
        For row As Integer = 0 To 5
            inputs.RowStyles.Add(New RowStyle(SizeType.Absolute, 44.0F))
        Next
        AddInput(inputs, 0, "Servidor SQL", serverBox, Nothing)
        ' Las bases van despues de usuario y password: con esos datos se carga la lista de bases.
        loadDatabasesButton.Text = "Elegir bases..."
        loadDatabasesButton.Dock = DockStyle.Fill
        AddHandler loadDatabasesButton.Click, AddressOf LoadDatabasesClicked
        AddInput(inputs, 1, "Usuario SQL", userBox, Nothing)
        AddInput(inputs, 2, "Password SQL", passwordBox, Nothing)
        AddInput(inputs, 3, "Bases de datos", databaseBox, loadDatabasesButton)

        Dim sourceButton As New Button With {.Text = "Examinar...", .Dock = DockStyle.Fill}
        AddHandler sourceButton.Click, Sub(sender, e) BrowseFolder(sourceBox)
        AddInput(inputs, 4, "Carpeta de scripts", sourceBox, sourceButton)
        Dim destinationButton As New Button With {.Text = "Examinar...", .Dock = DockStyle.Fill}
        AddHandler destinationButton.Click, Sub(sender, e) BrowseFolder(destinationBox)
        AddInput(inputs, 5, "Guardar respaldo en", destinationBox, destinationButton)
        root.Controls.Add(inputs, 0, 0)

        includeSubfoldersBox.Text = "Incluir subcarpetas de la carpeta de scripts"
        encryptBox.Text = "Cifrar conexion"
        encryptBox.Checked = True
        trustCertificateBox.Text = "Confiar en el certificado del servidor (sin validarlo)"
        trustCertificateBox.Checked = True
        AddHandler encryptBox.CheckedChanged, Sub(sender, e) trustCertificateBox.Enabled = encryptBox.Checked
        optionsPanel.Dock = DockStyle.Fill
        optionsPanel.WrapContents = False
        For Each box As CheckBox In {includeSubfoldersBox, encryptBox, trustCertificateBox}
            box.AutoSize = True
            box.Margin = New Padding(3, 6, 24, 3)
            optionsPanel.Controls.Add(box)
        Next
        root.Controls.Add(optionsPanel, 0, 1)

        Dim actions As New FlowLayoutPanel With {.Dock = DockStyle.Fill, .WrapContents = False}
        analyzeButton.Text = "Analizar"
        analyzeButton.Width = 105
        backupButton.Text = "Respaldar seleccionados"
        backupButton.Width = 180
        compareButton.Text = "Comparar"
        compareButton.Width = 105
        searchDatabasesButton.Text = "Buscar en bases"
        searchDatabasesButton.Width = 140
        cancelOperationButton.Text = "Cancelar"
        cancelOperationButton.Width = 145
        selectAllButton.Text = "Marcar listos"
        selectAllButton.Width = 115
        selectNoneButton.Text = "Desmarcar"
        selectNoneButton.Width = 105
        For Each button As Button In {analyzeButton, backupButton, compareButton, searchDatabasesButton, cancelOperationButton, selectAllButton, selectNoneButton}
            button.Height = 34
            actions.Controls.Add(button)
        Next
        AddHandler analyzeButton.Click, AddressOf AnalyzeClicked
        AddHandler backupButton.Click, AddressOf BackupClicked
        AddHandler compareButton.Click, AddressOf CompareClicked
        AddHandler searchDatabasesButton.Click, AddressOf SearchDatabasesClicked
        AddHandler cancelOperationButton.Click, AddressOf CancelOperationClicked
        AddHandler selectAllButton.Click, Sub(sender, e) SetAllSelection(True)
        AddHandler selectNoneButton.Click, Sub(sender, e) SetAllSelection(False)
        root.Controls.Add(actions, 0, 2)

        summaryLabel.Text = "Pulsa Analizar para ver los objetos antes del respaldo."
        summaryLabel.Dock = DockStyle.Fill
        root.Controls.Add(summaryLabel, 0, 3)

        ConfigureGrid()
        root.Controls.Add(objectsGrid, 0, 4)
        outputBox.Dock = DockStyle.Fill
        outputBox.Multiline = True
        outputBox.ReadOnly = True
        outputBox.ScrollBars = ScrollBars.Vertical
        outputBox.Text = "Se leeran los nombres reales desde las declaraciones de cada archivo .sql."
        root.Controls.Add(outputBox, 0, 5)
        Me.Controls.Add(root)
        ApplySettings(UserSettings.Load(UserSettings.DefaultPath))

        For Each box As TextBox In {serverBox, databaseBox, userBox, passwordBox, sourceBox, destinationBox}
            AddHandler box.TextChanged, AddressOf InputsChanged
        Next
        AddHandler includeSubfoldersBox.CheckedChanged, AddressOf InputsChanged
        RefreshActionButtons()
    End Sub

    Private Sub ApplySettings(settings As UserSettings)
        serverBox.Text = If(settings.Server, "")
        databaseBox.Text = If(settings.Database, "")
        userBox.Text = If(settings.UserName, "")
        sourceBox.Text = If(settings.SourceFolder, "")
        destinationBox.Text = If(String.IsNullOrWhiteSpace(settings.DestinationFolder),
                                 Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Respaldos_TE"),
                                 settings.DestinationFolder)
        includeSubfoldersBox.Checked = settings.IncludeSubfolders
        encryptBox.Checked = settings.Encrypt
        trustCertificateBox.Checked = settings.TrustServerCertificate
        trustCertificateBox.Enabled = encryptBox.Checked
    End Sub

    Private Sub SaveSettings()
        Dim settings As New UserSettings With {
            .Server = serverBox.Text.Trim(),
            .Database = databaseBox.Text.Trim(),
            .UserName = userBox.Text.Trim(),
            .SourceFolder = sourceBox.Text.Trim(),
            .DestinationFolder = destinationBox.Text.Trim(),
            .IncludeSubfolders = includeSubfoldersBox.Checked,
            .Encrypt = encryptBox.Checked,
            .TrustServerCertificate = trustCertificateBox.Checked
        }
        Try
            settings.Save(UserSettings.DefaultPath)
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
            ' No guardar la configuracion no debe impedir usar ni cerrar la aplicacion.
        End Try
    End Sub

    Protected Overrides Sub OnFormClosing(e As FormClosingEventArgs)
        SaveSettings()
        MyBase.OnFormClosing(e)
    End Sub

    Private Sub ConfigureGrid()
        objectsGrid.Dock = DockStyle.Fill
        objectsGrid.AllowUserToAddRows = False
        objectsGrid.AllowUserToDeleteRows = False
        objectsGrid.MultiSelect = True
        objectsGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect
        objectsGrid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells
        objectsGrid.RowHeadersVisible = False
        objectsGrid.Columns.Add(New DataGridViewCheckBoxColumn With {.HeaderText = "Guardar", .Width = 65})
        objectsGrid.Columns.Add(New DataGridViewTextBoxColumn With {.HeaderText = "Base", .Width = 180, .ReadOnly = True})
        objectsGrid.Columns.Add(New DataGridViewTextBoxColumn With {.HeaderText = "Tipo", .Width = 110, .ReadOnly = True})
        objectsGrid.Columns.Add(New DataGridViewTextBoxColumn With {.HeaderText = "Objeto declarado", .Width = 250, .ReadOnly = True})
        objectsGrid.Columns.Add(New DataGridViewTextBoxColumn With {.HeaderText = "Archivo", .Width = 220, .ReadOnly = True})
        objectsGrid.Columns.Add(New DataGridViewTextBoxColumn With {.HeaderText = "Estado", .Width = 160, .ReadOnly = True})
        objectsGrid.Columns.Add(New DataGridViewTextBoxColumn With {.HeaderText = "Detalle", .AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, .ReadOnly = True})
        AddHandler objectsGrid.CurrentCellDirtyStateChanged, AddressOf GridDirtyStateChanged
        AddHandler objectsGrid.CellValueChanged, Sub(sender, e) RefreshActionButtons()
        AddHandler objectsGrid.SelectionChanged, Sub(sender, e) RefreshActionButtons()
    End Sub

    Private Shared Sub AddInput(table As TableLayoutPanel, row As Integer, labelText As String, box As Control, button As Button)
        Dim label As New Label With {.Text = labelText, .AutoSize = True, .Anchor = AnchorStyles.Left}
        box.Dock = DockStyle.Fill
        box.Margin = New Padding(3, 8, 3, 8)
        table.Controls.Add(label, 0, row)
        table.Controls.Add(box, 1, row)
        If button IsNot Nothing Then
            button.Margin = New Padding(3, 5, 3, 5)
            table.Controls.Add(button, 2, row)
        End If
    End Sub

    Private Sub BrowseFolder(box As TextBox)
        Using dialog As New FolderBrowserDialog()
            If Directory.Exists(box.Text) Then dialog.SelectedPath = box.Text
            If dialog.ShowDialog(Me) = DialogResult.OK Then box.Text = dialog.SelectedPath
        End Using
    End Sub

    Private Sub InputsChanged(sender As Object, e As EventArgs)
        analysis = Nothing
        objectsGrid.Rows.Clear()
        summaryLabel.Text = "Los datos cambiaron. Pulsa Analizar de nuevo."
        outputBox.Text = "La vista previa anterior ya no corresponde a estos datos."
        RefreshActionButtons()
    End Sub

    Private Sub GridDirtyStateChanged(sender As Object, e As EventArgs)
        If objectsGrid.IsCurrentCellDirty Then objectsGrid.CommitEdit(DataGridViewDataErrorContexts.Commit)
    End Sub

    Private Sub RefreshActionButtons()
        If suppressRefresh Then Return
        analyzeButton.Enabled = Not busy
        Dim readyCount As Integer = 0
        Dim selectedCount As Integer = 0
        For Each row As DataGridViewRow In objectsGrid.Rows
            Dim item As AnalysisItem = TryCast(row.Tag, AnalysisItem)
            If item Is Nothing OrElse Not item.CanBackup Then Continue For
            readyCount += 1
            If Convert.ToBoolean(row.Cells(0).Value) Then selectedCount += 1
        Next
        backupButton.Enabled = Not busy AndAlso analysis IsNot Nothing AndAlso selectedCount > 0
        selectAllButton.Enabled = Not busy AndAlso analysis IsNot Nothing AndAlso readyCount > 0
        selectNoneButton.Enabled = selectAllButton.Enabled
        Dim current As AnalysisItem = SelectedItem()
        compareButton.Enabled = Not busy AndAlso current IsNot Nothing AndAlso current.CanBackup
        searchDatabasesButton.Enabled = Not busy AndAlso analysis IsNot Nothing AndAlso objectsGrid.SelectedRows.Count > 0
        cancelOperationButton.Enabled = operationCancellation IsNot Nothing AndAlso Not operationCancellation.IsCancellationRequested
    End Sub

    Private Function SelectedItem() As AnalysisItem
        If objectsGrid.CurrentRow Is Nothing Then Return Nothing
        Return TryCast(objectsGrid.CurrentRow.Tag, AnalysisItem)
    End Function

    Private Sub SetAllSelection(value As Boolean)
        ' Evita recalcular los botones por cada celda (CellValueChanged) al marcar muchas filas.
        suppressRefresh = True
        Try
            For Each row As DataGridViewRow In objectsGrid.Rows
                Dim item As AnalysisItem = TryCast(row.Tag, AnalysisItem)
                If item IsNot Nothing AndAlso item.CanBackup Then row.Cells(0).Value = value
            Next
        Finally
            suppressRefresh = False
        End Try
        RefreshActionButtons()
    End Sub

    Private Sub PopulateGrid(items As List(Of AnalysisItem))
        suppressRefresh = True
        objectsGrid.SuspendLayout()
        objectsGrid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None
        Try
            objectsGrid.Rows.Clear()
            Dim newRows As New List(Of DataGridViewRow)(items.Count)
            For Each item As AnalysisItem In items
                Dim fileLabel As String = Path.GetFileName(item.Declaration.SourceFile)
                If item.AlsoDeclaredIn IsNot Nothing AndAlso item.AlsoDeclaredIn.Count > 0 Then fileLabel &= " (+" & item.AlsoDeclaredIn.Count.ToString() & ")"
                Dim gridRow As New DataGridViewRow()
                gridRow.CreateCells(objectsGrid, item.CanBackup, item.DatabaseName, item.Declaration.Kind, item.Declaration.DisplayName(), fileLabel, item.Status, item.Detail)
                gridRow.Tag = item
                newRows.Add(gridRow)
            Next
            objectsGrid.Rows.AddRange(newRows.ToArray())
            For Each gridRow As DataGridViewRow In objectsGrid.Rows
                If Not DirectCast(gridRow.Tag, AnalysisItem).CanBackup Then gridRow.Cells(0).ReadOnly = True
            Next
        Finally
            objectsGrid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells
            objectsGrid.ResumeLayout()
            suppressRefresh = False
        End Try
        RefreshActionButtons()
    End Sub

    ' Bases escritas en el campo, separadas por coma o punto y coma, sin repetir.
    Private Function SelectedDatabases() As List(Of String)
        Return ParseDatabaseList(databaseBox.Text)
    End Function

    Friend Shared Function ParseDatabaseList(text As String) As List(Of String)
        Return text.Split({","c, ";"c}).Select(Function(x) x.Trim()).Where(Function(x) x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
    End Function

    Private Sub SetSelectedDatabases(names As IEnumerable(Of String))
        Dim list As List(Of String) = names.ToList()
        Dim current As List(Of String) = SelectedDatabases()
        ' Si el conjunto no cambia (aunque cambie el orden o las mayusculas) no se invalida la vista previa.
        If list.Count = current.Count AndAlso list.All(Function(x) current.Contains(x, StringComparer.OrdinalIgnoreCase)) Then Return
        databaseBox.Text = String.Join(", ", list)
    End Sub

    ' connectionOnly: solo se necesitan servidor, usuario y password (por ejemplo, para listar las bases).
    Private Function CreateRequest(connectionOnly As Boolean) As BackupRequest
        If connectionOnly Then
            If String.IsNullOrWhiteSpace(serverBox.Text) OrElse
               String.IsNullOrWhiteSpace(userBox.Text) OrElse
               String.IsNullOrEmpty(passwordBox.Text) Then
                Throw New InvalidOperationException("Completa servidor, usuario y password.")
            End If
        ElseIf String.IsNullOrWhiteSpace(serverBox.Text) OrElse
           SelectedDatabases().Count = 0 OrElse
           String.IsNullOrWhiteSpace(userBox.Text) OrElse
           String.IsNullOrEmpty(passwordBox.Text) OrElse
           String.IsNullOrWhiteSpace(sourceBox.Text) OrElse
           String.IsNullOrWhiteSpace(destinationBox.Text) Then
            Throw New InvalidOperationException("Completa servidor, bases, usuario, password, origen y destino.")
        End If
        Dim password As New SecureString()
        For Each character As Char In passwordBox.Text
            password.AppendChar(character)
        Next
        password.MakeReadOnly()
        Return New BackupRequest With {
            .Server = serverBox.Text.Trim(),
            .Databases = SelectedDatabases(),
            .UserName = userBox.Text.Trim(),
            .Password = password,
            .Encrypt = encryptBox.Checked,
            .TrustServerCertificate = encryptBox.Checked AndAlso trustCertificateBox.Checked,
            .SourceFolder = sourceBox.Text.Trim(),
            .DestinationFolder = destinationBox.Text.Trim(),
            .IncludeSubfolders = includeSubfoldersBox.Checked
        }
    End Function

    ' Ejecuta una operacion en segundo plano con la ventana bloqueada y se encarga de la cancelacion,
    ' los mensajes de error y la liberacion del password. Devuelve Nothing si se cancelo o fallo.
    Private Async Function RunOperationAsync(Of T As Class)(startMessage As String,
                                                           cancelledMessage As String,
                                                           errorPrefix As String,
                                                           errorTitle As String,
                                                           work As Func(Of BackupRequest, CancellationToken, IProgress(Of String), T),
                                                           Optional onError As Action = Nothing,
                                                           Optional connectionOnly As Boolean = False) As Task(Of T)
        Dim request As BackupRequest = Nothing
        Dim cancellation As CancellationTokenSource = Nothing
        Try
            request = CreateRequest(connectionOnly)
            cancellation = New CancellationTokenSource()
            SetBusy(True, cancellation)
            outputBox.Text = startMessage
            Dim token As CancellationToken = cancellation.Token
            Dim progress As IProgress(Of String) = New Progress(Of String)(Sub(message) ShowOperationProgress(cancellation, message))
            Return Await Task.Run(Function() work(request, token, progress))
        Catch ex As OperationCanceledException
            outputBox.Text = cancelledMessage
        Catch ex As Exception
            If onError IsNot Nothing Then onError()
            outputBox.Text = errorPrefix & ex.Message
            MessageBox.Show(Me, ex.Message, errorTitle, MessageBoxButtons.OK, MessageBoxIcon.Error)
        Finally
            SetBusy(False, Nothing)
            If cancellation IsNot Nothing Then cancellation.Dispose()
            If request IsNot Nothing AndAlso request.Password IsNot Nothing Then request.Password.Dispose()
        End Try
        Return Nothing
    End Function

    Private Sub SetBusy(value As Boolean, cancellation As CancellationTokenSource)
        busy = value
        operationCancellation = cancellation
        inputs.Enabled = Not value
        optionsPanel.Enabled = Not value
        RefreshActionButtons()
    End Sub

    Private Async Sub LoadDatabasesClicked(sender As Object, e As EventArgs)
        Dim names As List(Of String) = Await RunOperationAsync(Of List(Of String))(
            "Conectando al servidor y obteniendo las bases de datos...",
            "Carga de bases cancelada.",
            "No se pudieron obtener las bases: ", "Error de conexion",
            Function(request, token, progress) BackupService.ListDatabases(request, token),
            connectionOnly:=True)
        If names Is Nothing Then Return

        If names.Count = 0 Then
            outputBox.Text = "Conexion correcta, pero el usuario no tiene acceso a ninguna base de datos de usuario."
            Return
        End If
        Using dialog As New DatabasePickerForm(names, SelectedDatabases())
            If dialog.ShowDialog(Me) = DialogResult.OK Then
                SetSelectedDatabases(dialog.SelectedDatabases)
                outputBox.Text = "Bases elegidas: " & dialog.SelectedDatabases.Count.ToString() & " de " & names.Count.ToString() & " disponibles."
            Else
                outputBox.Text = "Conexion correcta: " & names.Count.ToString() & " base(s) disponible(s). No se cambio la seleccion."
            End If
        End Using
    End Sub

    Private Async Sub AnalyzeClicked(sender As Object, e As EventArgs)
        Dim result As BackupAnalysis = Await RunOperationAsync(Of BackupAnalysis)(
            "Leyendo scripts y consultando la base de datos...",
            "Analisis cancelado. No se modifico ninguna base de datos.",
            "No se pudo analizar: ", "Error de analisis",
            Function(request, token, progress) BackupService.Analyze(request, token, progress),
            Sub()
                analysis = Nothing
                objectsGrid.Rows.Clear()
            End Sub)
        If result Is Nothing Then Return

        analysis = result
        PopulateGrid(result.Items)
        SaveSettings()
        Dim ready As Integer = result.Items.Where(Function(x) x.CanBackup).Count()
        Dim databaseCount As Integer = result.Items.Select(Function(x) x.DatabaseName).Distinct(StringComparer.OrdinalIgnoreCase).Count()
        summaryLabel.Text = result.Items.Count.ToString() & " filas (objeto por base) en " & databaseCount.ToString() & " base(s); " &
                            ready.ToString() & " listas; " & (result.Items.Count - ready).ToString() & " requieren revision."
        outputBox.Text = "Marca objetos para el respaldo. Selecciona filas (Ctrl/Shift) para Comparar o Buscar en bases."
        If result.FilesWithoutObject.Count > 0 Then
            outputBox.AppendText(vbCrLf & result.FilesWithoutObject.Count.ToString() & " archivo(s) .sql sin declaraciones reconocidas.")
        End If
    End Sub

    Private Sub CompareClicked(sender As Object, e As EventArgs)
        Dim item As AnalysisItem = SelectedItem()
        If item Is Nothing OrElse Not item.CanBackup Then Return
        Using dialog As New CompareForm(item)
            dialog.ShowDialog(Me)
        End Using
    End Sub

    Private Async Sub SearchDatabasesClicked(sender As Object, e As EventArgs)
        If analysis Is Nothing Then Return
        Dim selectedObjects As New List(Of DeclaredObject)()
        For Each row As DataGridViewRow In objectsGrid.SelectedRows
            Dim item As AnalysisItem = TryCast(row.Tag, AnalysisItem)
            If item IsNot Nothing AndAlso Not selectedObjects.Contains(item.Declaration) Then selectedObjects.Add(item.Declaration)
        Next
        If selectedObjects.Count = 0 Then Return

        Dim report As DatabaseSearchReport = Await RunOperationAsync(Of DatabaseSearchReport)(
            "Buscando los nombres seleccionados en las bases visibles del servidor...",
            "Busqueda cancelada. No se modifico ninguna base de datos.",
            "No se pudo buscar en las bases: ", "Error de busqueda",
            Function(request, token, progress) BackupService.FindDatabases(request, selectedObjects, token, progress))
        If report Is Nothing Then Return

        outputBox.Text = "Buscadas " & report.ScannedDatabases.ToString() & " bases accesibles; " &
                         report.Locations.Count.ToString() & " coincidencias visibles."
        Using dialog As New DatabaseSearchForm(report)
            If dialog.ShowDialog(Me) = DialogResult.OK AndAlso dialog.SelectedDatabases.Count > 0 Then
                SetSelectedDatabases(SelectedDatabases().Concat(dialog.SelectedDatabases).Distinct(StringComparer.OrdinalIgnoreCase))
            End If
        End Using
    End Sub

    Private Sub ShowOperationProgress(cancellation As CancellationTokenSource, message As String)
        If Object.ReferenceEquals(operationCancellation, cancellation) AndAlso Not cancellation.IsCancellationRequested AndAlso Not Me.IsDisposed Then outputBox.Text = message
    End Sub

    Private Sub CancelOperationClicked(sender As Object, e As EventArgs)
        If operationCancellation Is Nothing Then Return
        cancelOperationButton.Enabled = False
        outputBox.Text = "Cancelando operacion; espera a que termine el paso actual..."
        operationCancellation.Cancel()
    End Sub

    Private Async Sub BackupClicked(sender As Object, e As EventArgs)
        If analysis Is Nothing Then Return
        objectsGrid.EndEdit()
        Dim selected As New List(Of String)()
        For Each row As DataGridViewRow In objectsGrid.Rows
            Dim item As AnalysisItem = TryCast(row.Tag, AnalysisItem)
            If item IsNot Nothing AndAlso item.CanBackup AndAlso Convert.ToBoolean(row.Cells(0).Value) Then selected.Add(item.Key)
        Next
        If selected.Count = 0 Then Return

        Dim currentAnalysis As BackupAnalysis = analysis
        Dim result As BackupResult = Await RunOperationAsync(Of BackupResult)(
            "Comprobando que SQL Server no cambio desde la vista previa y guardando archivos...",
            "Respaldo cancelado. No se creo una carpeta final; se limpiaron los archivos temporales.",
            "No se pudo crear el respaldo: ", "Error de respaldo",
            Function(request, token, progress) BackupService.Save(request, currentAnalysis, selected, token, progress))
        If result Is Nothing Then Return

        outputBox.Text = "Respaldo creado: " & result.Folder & vbCrLf &
                         "Objetos guardados: " & result.SavedObjects.Count.ToString() & vbCrLf &
                         "Archivos SQL verificados: " & result.VerifiedFiles.ToString() & vbCrLf &
                         "Objetos omitidos: " & result.Skipped.Count.ToString() & vbCrLf & vbCrLf &
                         String.Join(vbCrLf, result.SavedObjects)
        MessageBox.Show(Me, "Respaldo terminado y archivos verificados.", "Resultado", MessageBoxButtons.OK, MessageBoxIcon.Information)
    End Sub
End Class

Friend NotInheritable Class CompareForm
    Inherits Form

    Private NotInheritable Class DiffView
        Public Property Declaration As DeclaredObject
        Public Property Title As String
        Public Property Page As TabPage
        Public Property Box As RichTextBox
    End Class

    Private ReadOnly item As AnalysisItem
    Private ReadOnly ignoreWhitespaceBox As New CheckBox()
    Private ReadOnly showAllBox As New CheckBox()
    Private ReadOnly views As New List(Of DiffView)()

    Public Sub New(item As AnalysisItem)
        Me.item = item
        Me.Text = "Comparar [" & item.DatabaseName & "] " & item.Declaration.DisplayName()
        Me.StartPosition = FormStartPosition.CenterParent
        Me.Size = New Size(1100, 750)
        Me.MinimumSize = New Size(800, 500)

        Dim root As New TableLayoutPanel With {.Dock = DockStyle.Fill, .ColumnCount = 1, .RowCount = 2}
        root.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100.0F))
        root.RowStyles.Add(New RowStyle(SizeType.Absolute, 34.0F))
        root.RowStyles.Add(New RowStyle(SizeType.Percent, 100.0F))

        Dim options As New FlowLayoutPanel With {.Dock = DockStyle.Fill, .WrapContents = False}
        ignoreWhitespaceBox.Text = "Ignorar diferencias de espacios y tabulaciones"
        showAllBox.Text = "Mostrar el objeto completo"
        For Each box As CheckBox In {ignoreWhitespaceBox, showAllBox}
            box.AutoSize = True
            box.Margin = New Padding(6, 8, 24, 3)
            options.Controls.Add(box)
            AddHandler box.CheckedChanged, Sub(sender, e) RenderDiffs()
        Next
        root.Controls.Add(options, 0, 0)

        Dim tabs As New TabControl With {.Dock = DockStyle.Fill}
        Dim declarations As New List(Of DeclaredObject) From {item.Declaration}
        If item.AlsoDeclaredIn IsNot Nothing Then declarations.AddRange(item.AlsoDeclaredIn)
        Dim hasDuplicates As Boolean = declarations.Count > 1
        For Each declaration As DeclaredObject In declarations
            Dim suffix As String = If(hasDuplicates, " - " & Path.GetFileName(declaration.SourceFile), "")
            Dim view As New DiffView With {
                .Declaration = declaration,
                .Title = "Diferencias" & suffix,
                .Page = New TabPage("Diferencias" & suffix),
                .Box = CreateEditor()
            }
            view.Page.Controls.Add(view.Box)
            tabs.TabPages.Add(view.Page)
            views.Add(view)
        Next
        AddTextTab(tabs, "Actual en SQL Server", item.Current.Definition)
        For Each declaration As DeclaredObject In declarations
            AddTextTab(tabs, "Archivo de liberacion" & If(hasDuplicates, " - " & Path.GetFileName(declaration.SourceFile), ""), declaration.CandidateText)
        Next
        root.Controls.Add(tabs, 0, 1)
        Me.Controls.Add(root)
        RenderDiffs()
    End Sub

    Private Sub RenderDiffs()
        For Each view As DiffView In views
            Dim result As DiffResult = DiffService.Compare(item.Current.Definition, view.Declaration.CandidateText, ignoreWhitespaceBox.Checked)
            Dim lines As List(Of DiffLine) = If(showAllBox.Checked, result.Lines, DiffService.Collapse(result.Lines, 3))
            view.Box.Rtf = BuildRtf(result, lines, ignoreWhitespaceBox.Checked)
            view.Page.Text = view.Title & If(result.HasDifferences,
                                             " (-" & result.Removed.ToString() & " +" & result.Added.ToString() & ")",
                                             " (iguales)")
        Next
    End Sub

    Private Shared Function CreateEditor() As RichTextBox
        Return New RichTextBox With {
            .Dock = DockStyle.Fill,
            .ReadOnly = True,
            .WordWrap = False,
            .BackColor = SystemColors.Window,
            .Font = New Font("Consolas", 10.0F)
        }
    End Function

    Private Shared Sub AddTextTab(tabs As TabControl, title As String, content As String)
        Dim page As New TabPage(title)
        Dim editor As RichTextBox = CreateEditor()
        editor.Text = content
        page.Controls.Add(editor)
        tabs.TabPages.Add(page)
    End Sub

    ' Colores: 1 gris (numeros de linea), 2 rojo (eliminado), 3 verde (agregado), 4 azul (tramos ocultos y encabezado).
    Friend Shared Function BuildRtf(result As DiffResult, lines As List(Of DiffLine), ignoreWhitespace As Boolean) As String
        Dim rtf As New StringBuilder()
        rtf.Append("{\rtf1\ansi\deff0{\fonttbl{\f0\fmodern Consolas;}}")
        rtf.Append("{\colortbl ;\red120\green120\blue120;\red178\green0\blue0;\red0\green120\blue0;\red0\green70\blue160;}")
        rtf.Append("\f0\fs20 ")

        Dim ignored As String = "Se ignoran los comentarios antes del CREATE, CREATE/ALTER/CREATE OR ALTER y los espacios al final de linea" &
                                If(ignoreWhitespace, ", ademas de las diferencias de espacios y tabulaciones.", ".")
        rtf.Append("\cf4\b ")
        If result.HasDifferences Then
            AppendRtfText(rtf, result.Removed.ToString() & " linea(s) solo en SQL Server (-), " & result.Added.ToString() & " linea(s) solo en el archivo (+).")
        Else
            AppendRtfText(rtf, "Sin diferencias.")
        End If
        rtf.Append("\b0\par ")
        AppendRtfText(rtf, ignored)
        rtf.Append("\par ")
        If result.Truncated Then
            AppendRtfText(rtf, "Las versiones son muy distintas: una parte se muestra como reemplazo completo.")
            rtf.Append("\par ")
        End If
        rtf.Append("\cf1 ")
        AppendRtfText(rtf, "  SQL  Arch.")
        rtf.Append("\par\par ")

        For Each line As DiffLine In lines
            If line.Kind = DiffKind.Skipped Then
                rtf.Append("\cf4\i ")
                AppendRtfText(rtf, "            " & line.Text)
                rtf.Append("\i0\par ")
                Continue For
            End If
            Dim oldNumber As String = If(line.Kind = DiffKind.Added, "", line.OldNumber.ToString())
            Dim newNumber As String = If(line.Kind = DiffKind.Removed, "", line.NewNumber.ToString())
            rtf.Append("\cf1 ")
            AppendRtfText(rtf, oldNumber.PadLeft(5) & " " & newNumber.PadLeft(5) & " ")
            Select Case line.Kind
                Case DiffKind.Removed
                    rtf.Append("\cf2 ")
                    AppendRtfText(rtf, "- " & line.Text)
                Case DiffKind.Added
                    rtf.Append("\cf3 ")
                    AppendRtfText(rtf, "+ " & line.Text)
                Case Else
                    rtf.Append("\cf0 ")
                    AppendRtfText(rtf, "  " & line.Text)
            End Select
            rtf.Append("\par ")
        Next
        rtf.Append("}")
        Return rtf.ToString()
    End Function

    Private Shared Sub AppendRtfText(rtf As StringBuilder, text As String)
        For Each character As Char In text
            Select Case character
                Case "\"c
                    rtf.Append("\\")
                Case "{"c
                    rtf.Append("\{")
                Case "}"c
                    rtf.Append("\}")
                Case ControlChars.Tab
                    rtf.Append("\tab ")
                Case Else
                    Dim code As Integer = AscW(character)
                    If code > 126 Then
                        If code > 32767 Then code -= 65536
                        rtf.Append("\u").Append(code.ToString()).Append("?")
                    ElseIf code >= 32 Then
                        rtf.Append(character)
                    End If
            End Select
        Next
    End Sub
End Class

Friend NotInheritable Class DatabaseSearchForm
    Inherits Form

    Private ReadOnly locationsGrid As New DataGridView()
    Private ReadOnly chooseButton As New Button()
    Public ReadOnly Property SelectedDatabases As New List(Of String)()

    Public Sub New(report As DatabaseSearchReport)
        Me.Text = "Bases donde aparecen los objetos"
        Me.StartPosition = FormStartPosition.CenterParent
        Me.Size = New Size(900, 630)
        Me.MinimumSize = New Size(720, 480)

        Dim root As New TableLayoutPanel With {.Dock = DockStyle.Fill, .Padding = New Padding(12), .ColumnCount = 1, .RowCount = 4}
        root.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100.0F))
        root.RowStyles.Add(New RowStyle(SizeType.Absolute, 48.0F))
        root.RowStyles.Add(New RowStyle(SizeType.Percent, 100.0F))
        root.RowStyles.Add(New RowStyle(SizeType.Absolute, 155.0F))
        root.RowStyles.Add(New RowStyle(SizeType.Absolute, 45.0F))

        Dim summary As New Label With {
            .Dock = DockStyle.Fill,
            .Text = "Bases accesibles revisadas: " & report.ScannedDatabases.ToString() &
                    " | Coincidencias visibles: " & report.Locations.Count.ToString() &
                    " | Omitidas: " & report.SkippedDatabases.Count.ToString() &
                    ". La ausencia de una fila no prueba que el objeto no exista."
        }
        root.Controls.Add(summary, 0, 0)

        locationsGrid.Dock = DockStyle.Fill
        locationsGrid.ReadOnly = True
        locationsGrid.AllowUserToAddRows = False
        locationsGrid.AllowUserToDeleteRows = False
        locationsGrid.MultiSelect = True
        locationsGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect
        locationsGrid.RowHeadersVisible = False
        locationsGrid.Columns.Add("BaseDatos", "Base de datos")
        locationsGrid.Columns.Add("Tipo", "Tipo")
        locationsGrid.Columns.Add("Esquema", "Esquema")
        locationsGrid.Columns.Add("Objeto", "Objeto")
        locationsGrid.Columns(0).Width = 250
        locationsGrid.Columns(1).Width = 120
        locationsGrid.Columns(2).Width = 150
        locationsGrid.Columns(3).AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill
        For Each location As DatabaseLocation In report.Locations
            locationsGrid.Rows.Add(location.DatabaseName, location.Kind, If(location.SchemaName, ""), location.ObjectName)
        Next
        root.Controls.Add(locationsGrid, 0, 1)

        Dim notes As String = If(report.NamesWithoutVisibleMatch.Count = 0,
                                 "Todos los nombres seleccionados tuvieron al menos una coincidencia visible.",
                                 "Sin coincidencia visible:" & vbCrLf & String.Join(vbCrLf, report.NamesWithoutVisibleMatch))
        notes &= vbCrLf & vbCrLf & "Tiempo y resultado por base:" & vbCrLf & String.Join(vbCrLf, report.DatabaseTimings)
        Dim skippedBox As New TextBox With {
            .Dock = DockStyle.Fill,
            .Multiline = True,
            .ReadOnly = True,
            .ScrollBars = ScrollBars.Vertical,
            .Text = notes
        }
        root.Controls.Add(skippedBox, 0, 2)

        Dim actions As New FlowLayoutPanel With {.Dock = DockStyle.Fill}
        chooseButton.Text = "Agregar bases seleccionadas"
        chooseButton.Width = 210
        chooseButton.Enabled = report.Locations.Count > 0
        AddHandler chooseButton.Click, AddressOf ChooseDatabase
        Dim closeButton As New Button With {.Text = "Cerrar", .Width = 90, .DialogResult = DialogResult.Cancel}
        actions.Controls.Add(chooseButton)
        actions.Controls.Add(closeButton)
        root.Controls.Add(actions, 0, 3)
        Me.Controls.Add(root)
        Me.CancelButton = closeButton
    End Sub

    Private Sub ChooseDatabase(sender As Object, e As EventArgs)
        ' Filas seleccionadas (Ctrl/Shift); si no hay ninguna, la fila actual.
        Dim rows As IEnumerable(Of DataGridViewRow) = locationsGrid.SelectedRows.Cast(Of DataGridViewRow)()
        If Not rows.Any() AndAlso locationsGrid.CurrentRow IsNot Nothing Then rows = {locationsGrid.CurrentRow}
        For Each row As DataGridViewRow In rows.OrderBy(Function(x) x.Index)
            Dim name As String = Convert.ToString(row.Cells(0).Value)
            If Not SelectedDatabases.Contains(name, StringComparer.OrdinalIgnoreCase) Then SelectedDatabases.Add(name)
        Next
        If SelectedDatabases.Count = 0 Then Return
        Me.DialogResult = DialogResult.OK
        Me.Close()
    End Sub
End Class
