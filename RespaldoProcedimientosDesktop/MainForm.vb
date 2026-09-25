Imports System
Imports System.Collections.Generic
Imports System.Drawing
Imports System.IO
Imports System.Linq
Imports System.Text
Imports System.Threading
Imports System.Threading.Tasks
Imports System.Windows.Forms
Imports Microsoft.VisualBasic

Friend NotInheritable Class MainForm
    Inherits Form

    Private ReadOnly serverBox As New TextBox()
    Private ReadOnly databaseBox As New TextBox()
    Private ReadOnly userBox As New TextBox()
    Private ReadOnly passwordBox As New TextBox()
    Private ReadOnly sourceBox As New TextBox()
    Private ReadOnly destinationBox As New TextBox()
    Private ReadOnly includeSubfoldersBox As New CheckBox()
    Private ReadOnly analyzeButton As New Button()
    Private ReadOnly backupButton As New Button()
    Private ReadOnly compareButton As New Button()
    Private ReadOnly searchDatabasesButton As New Button()
    Private ReadOnly cancelButton As New Button()
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

        databaseBox.Text = "AdministradorEmpleado_PEMSACELAYA"
        sourceBox.Text = "C:\Users\Alonso Salinas\OneDrive\Desktop\Respaldos-SPs\Gestor Tiempo Extra\Fase 1\6 Reportes - Falta Liberar\Liberar Fase 1 TE\Interfaz"
        destinationBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Respaldos_TE")
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
        AddInput(inputs, 1, "Base de datos", databaseBox, Nothing)
        AddInput(inputs, 2, "Usuario SQL", userBox, Nothing)
        AddInput(inputs, 3, "Password SQL", passwordBox, Nothing)

        Dim sourceButton As New Button With {.Text = "Examinar...", .Dock = DockStyle.Fill}
        AddHandler sourceButton.Click, Sub(sender, e) BrowseFolder(sourceBox)
        AddInput(inputs, 4, "Carpeta de scripts", sourceBox, sourceButton)
        Dim destinationButton As New Button With {.Text = "Examinar...", .Dock = DockStyle.Fill}
        AddHandler destinationButton.Click, Sub(sender, e) BrowseFolder(destinationBox)
        AddInput(inputs, 5, "Guardar respaldo en", destinationBox, destinationButton)
        root.Controls.Add(inputs, 0, 0)

        includeSubfoldersBox.Text = "Incluir subcarpetas de la carpeta de scripts"
        includeSubfoldersBox.Dock = DockStyle.Fill
        root.Controls.Add(includeSubfoldersBox, 0, 1)

        Dim actions As New FlowLayoutPanel With {.Dock = DockStyle.Fill, .WrapContents = False}
        analyzeButton.Text = "Analizar"
        analyzeButton.Width = 105
        backupButton.Text = "Respaldar seleccionados"
        backupButton.Width = 180
        compareButton.Text = "Comparar"
        compareButton.Width = 105
        searchDatabasesButton.Text = "Buscar en bases"
        searchDatabasesButton.Width = 140
        cancelButton.Text = "Cancelar"
        cancelButton.Width = 145
        selectAllButton.Text = "Marcar listos"
        selectAllButton.Width = 115
        selectNoneButton.Text = "Desmarcar"
        selectNoneButton.Width = 105
        For Each button As Button In {analyzeButton, backupButton, compareButton, searchDatabasesButton, cancelButton, selectAllButton, selectNoneButton}
            button.Height = 34
            actions.Controls.Add(button)
        Next
        AddHandler analyzeButton.Click, AddressOf AnalyzeClicked
        AddHandler backupButton.Click, AddressOf BackupClicked
        AddHandler compareButton.Click, AddressOf CompareClicked
        AddHandler searchDatabasesButton.Click, AddressOf SearchDatabasesClicked
        AddHandler cancelButton.Click, AddressOf CancelOperationClicked
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

        For Each box As TextBox In {serverBox, databaseBox, userBox, passwordBox, sourceBox, destinationBox}
            AddHandler box.TextChanged, AddressOf InputsChanged
        Next
        AddHandler includeSubfoldersBox.CheckedChanged, AddressOf InputsChanged
        RefreshActionButtons()
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
        objectsGrid.Columns.Add(New DataGridViewTextBoxColumn With {.HeaderText = "Tipo", .Width = 110, .ReadOnly = True})
        objectsGrid.Columns.Add(New DataGridViewTextBoxColumn With {.HeaderText = "Objeto declarado", .Width = 250, .ReadOnly = True})
        objectsGrid.Columns.Add(New DataGridViewTextBoxColumn With {.HeaderText = "Archivo", .Width = 220, .ReadOnly = True})
        objectsGrid.Columns.Add(New DataGridViewTextBoxColumn With {.HeaderText = "Estado", .Width = 160, .ReadOnly = True})
        objectsGrid.Columns.Add(New DataGridViewTextBoxColumn With {.HeaderText = "Detalle", .AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, .ReadOnly = True})
        AddHandler objectsGrid.CurrentCellDirtyStateChanged, AddressOf GridDirtyStateChanged
        AddHandler objectsGrid.CellValueChanged, Sub(sender, e) RefreshActionButtons()
        AddHandler objectsGrid.SelectionChanged, Sub(sender, e) RefreshActionButtons()
    End Sub

    Private Shared Sub AddInput(table As TableLayoutPanel, row As Integer, labelText As String, box As TextBox, button As Button)
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
        cancelButton.Enabled = operationCancellation IsNot Nothing AndAlso Not operationCancellation.IsCancellationRequested
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
                gridRow.CreateCells(objectsGrid, item.CanBackup, item.Declaration.Kind, item.Declaration.DisplayName(), fileLabel, item.Status, item.Detail)
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

    Private Function CreateRequest() As BackupRequest
        If String.IsNullOrWhiteSpace(serverBox.Text) OrElse
           String.IsNullOrWhiteSpace(databaseBox.Text) OrElse
           String.IsNullOrWhiteSpace(userBox.Text) OrElse
           String.IsNullOrEmpty(passwordBox.Text) OrElse
           String.IsNullOrWhiteSpace(sourceBox.Text) OrElse
           String.IsNullOrWhiteSpace(destinationBox.Text) Then
            Throw New InvalidOperationException("Completa servidor, base, usuario, password, origen y destino.")
        End If
        Return New BackupRequest With {
            .Server = serverBox.Text.Trim(),
            .Database = databaseBox.Text.Trim(),
            .UserName = userBox.Text.Trim(),
            .Password = passwordBox.Text,
            .SourceFolder = sourceBox.Text.Trim(),
            .DestinationFolder = destinationBox.Text.Trim(),
            .IncludeSubfolders = includeSubfoldersBox.Checked
        }
    End Function

    Private Async Sub AnalyzeClicked(sender As Object, e As EventArgs)
        Dim request As BackupRequest = Nothing
        Dim cancellation As CancellationTokenSource = Nothing
        Try
            request = CreateRequest()
            cancellation = New CancellationTokenSource()
            operationCancellation = cancellation
            busy = True
            inputs.Enabled = False
            includeSubfoldersBox.Enabled = False
            RefreshActionButtons()
            outputBox.Text = "Leyendo scripts y consultando la base de datos..."
            Dim progress As IProgress(Of String) = New Progress(Of String)(Sub(message) ShowOperationProgress(cancellation, message))
            Dim result As BackupAnalysis = Await Task.Run(Function() BackupService.Analyze(request, cancellation.Token, progress))
            operationCancellation = Nothing
            analysis = result
            PopulateGrid(result.Items)
            Dim ready As Integer = result.Items.Where(Function(x) x.CanBackup).Count()
            summaryLabel.Text = result.Items.Count.ToString() & " objetos detectados; " & ready.ToString() & " listos; " &
                                (result.Items.Count - ready).ToString() & " requieren revision."
            outputBox.Text = "Marca objetos para el respaldo. Selecciona filas (Ctrl/Shift) para Comparar o Buscar en bases."
            If result.FilesWithoutObject.Count > 0 Then
                outputBox.AppendText(vbCrLf & result.FilesWithoutObject.Count.ToString() & " archivo(s) .sql sin declaraciones reconocidas.")
            End If
        Catch ex As OperationCanceledException
            outputBox.Text = "Analisis cancelado. No se modifico ninguna base de datos."
        Catch ex As Exception
            analysis = Nothing
            objectsGrid.Rows.Clear()
            outputBox.Text = "No se pudo analizar: " & ex.Message
            MessageBox.Show(Me, ex.Message, "Error de analisis", MessageBoxButtons.OK, MessageBoxIcon.Error)
        Finally
            operationCancellation = Nothing
            If cancellation IsNot Nothing Then cancellation.Dispose()
            If request IsNot Nothing Then request.Password = Nothing
            busy = False
            inputs.Enabled = True
            includeSubfoldersBox.Enabled = True
            RefreshActionButtons()
        End Try
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
            If item IsNot Nothing Then selectedObjects.Add(item.Declaration)
        Next
        If selectedObjects.Count = 0 Then Return

        Dim request As BackupRequest = Nothing
        Dim cancellation As CancellationTokenSource = Nothing
        Try
            request = CreateRequest()
            cancellation = New CancellationTokenSource()
            operationCancellation = cancellation
            busy = True
            inputs.Enabled = False
            includeSubfoldersBox.Enabled = False
            RefreshActionButtons()
            outputBox.Text = "Buscando los nombres seleccionados en las bases visibles del servidor..."
            Dim progress As IProgress(Of String) = New Progress(Of String)(Sub(message) ShowOperationProgress(cancellation, message))
            Dim report As DatabaseSearchReport = Await Task.Run(Function() BackupService.FindDatabases(request, selectedObjects, cancellation.Token, progress))
            operationCancellation = Nothing
            RefreshActionButtons()
            outputBox.Text = "Buscadas " & report.ScannedDatabases.ToString() & " bases accesibles; " &
                             report.Locations.Count.ToString() & " coincidencias visibles."
            Using dialog As New DatabaseSearchForm(report)
                If dialog.ShowDialog(Me) = DialogResult.OK AndAlso Not String.IsNullOrEmpty(dialog.SelectedDatabase) Then
                    databaseBox.Text = dialog.SelectedDatabase
                End If
            End Using
        Catch ex As OperationCanceledException
            outputBox.Text = "Busqueda cancelada. No se modifico ninguna base de datos."
        Catch ex As Exception
            outputBox.Text = "No se pudo buscar en las bases: " & ex.Message
            MessageBox.Show(Me, ex.Message, "Error de busqueda", MessageBoxButtons.OK, MessageBoxIcon.Error)
        Finally
            operationCancellation = Nothing
            If cancellation IsNot Nothing Then cancellation.Dispose()
            If request IsNot Nothing Then request.Password = Nothing
            busy = False
            inputs.Enabled = True
            includeSubfoldersBox.Enabled = True
            RefreshActionButtons()
        End Try
    End Sub

    Private Sub ShowOperationProgress(cancellation As CancellationTokenSource, message As String)
        If Object.ReferenceEquals(operationCancellation, cancellation) AndAlso Not cancellation.IsCancellationRequested AndAlso Not Me.IsDisposed Then outputBox.Text = message
    End Sub

    Private Sub CancelOperationClicked(sender As Object, e As EventArgs)
        If operationCancellation Is Nothing Then Return
        cancelButton.Enabled = False
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

        Dim request As BackupRequest = Nothing
        Dim cancellation As CancellationTokenSource = Nothing
        Try
            request = CreateRequest()
            cancellation = New CancellationTokenSource()
            operationCancellation = cancellation
            busy = True
            inputs.Enabled = False
            includeSubfoldersBox.Enabled = False
            RefreshActionButtons()
            outputBox.Text = "Comprobando que SQL Server no cambio desde la vista previa y guardando archivos..."
            Dim progress As IProgress(Of String) = New Progress(Of String)(Sub(message) ShowOperationProgress(cancellation, message))
            Dim result As BackupResult = Await Task.Run(Function() BackupService.Save(request, analysis, selected, cancellation.Token, progress))
            operationCancellation = Nothing
            outputBox.Text = "Respaldo creado: " & result.Folder & vbCrLf &
                             "Objetos guardados: " & result.SavedObjects.Count.ToString() & vbCrLf &
                             "Archivos SQL verificados: " & result.VerifiedFiles.ToString() & vbCrLf &
                             "Objetos omitidos: " & result.Skipped.Count.ToString() & vbCrLf & vbCrLf &
                             String.Join(vbCrLf, result.SavedObjects)
            MessageBox.Show(Me, "Respaldo terminado y archivos verificados.", "Resultado", MessageBoxButtons.OK, MessageBoxIcon.Information)
        Catch ex As OperationCanceledException
            outputBox.Text = "Respaldo cancelado. No se creo una carpeta final; se limpiaron los archivos temporales."
        Catch ex As Exception
            outputBox.Text = "No se pudo crear el respaldo: " & ex.Message
            MessageBox.Show(Me, ex.Message, "Error de respaldo", MessageBoxButtons.OK, MessageBoxIcon.Error)
        Finally
            operationCancellation = Nothing
            If cancellation IsNot Nothing Then cancellation.Dispose()
            If request IsNot Nothing Then request.Password = Nothing
            busy = False
            inputs.Enabled = True
            includeSubfoldersBox.Enabled = True
            RefreshActionButtons()
        End Try
    End Sub
End Class

Friend NotInheritable Class CompareForm
    Inherits Form

    Public Sub New(item As AnalysisItem)
        Me.Text = "Comparar " & item.Declaration.DisplayName()
        Me.StartPosition = FormStartPosition.CenterParent
        Me.Size = New Size(1100, 750)
        Me.MinimumSize = New Size(800, 500)

        Dim tabs As New TabControl With {.Dock = DockStyle.Fill}
        Dim hasDuplicates As Boolean = item.AlsoDeclaredIn IsNot Nothing AndAlso item.AlsoDeclaredIn.Count > 0
        Dim firstSuffix As String = If(hasDuplicates, " - " & Path.GetFileName(item.Declaration.SourceFile), "")
        AddTab(tabs, "Diferencias" & firstSuffix, DiffService.Build(item.Current.Definition, item.Declaration.CandidateText))
        AddTab(tabs, "Actual en SQL Server", item.Current.Definition)
        AddTab(tabs, "Archivo de liberacion" & firstSuffix, item.Declaration.CandidateText)
        If hasDuplicates Then
            For Each other As DeclaredObject In item.AlsoDeclaredIn
                Dim suffix As String = " - " & Path.GetFileName(other.SourceFile)
                AddTab(tabs, "Diferencias" & suffix, DiffService.Build(item.Current.Definition, other.CandidateText))
                AddTab(tabs, "Archivo de liberacion" & suffix, other.CandidateText)
            Next
        End If
        Me.Controls.Add(tabs)
    End Sub

    Private Shared Sub AddTab(tabs As TabControl, title As String, content As String)
        Dim page As New TabPage(title)
        Dim editor As New RichTextBox With {
            .Dock = DockStyle.Fill,
            .ReadOnly = True,
            .WordWrap = False,
            .Font = New Font("Consolas", 10.0F),
            .Text = content
        }
        page.Controls.Add(editor)
        tabs.TabPages.Add(page)
    End Sub
End Class

Friend NotInheritable Class DiffService
    Private Sub New()
    End Sub

    Public Shared Function Build(currentDefinition As String, candidateDefinition As String) As String
        Dim currentLines As String() = Normalize(currentDefinition).Split({vbLf}, StringSplitOptions.None)
        Dim candidateLines As String() = Normalize(candidateDefinition).Split({vbLf}, StringSplitOptions.None)
        Dim output As New StringBuilder()
        output.AppendLine("- Version actual en SQL Server")
        output.AppendLine("+ Version del archivo .sql")
        output.AppendLine()

        Dim oldIndex As Integer = 0
        Dim newIndex As Integer = 0
        While oldIndex < currentLines.Length OrElse newIndex < candidateLines.Length
            If output.Length > 2000000 Then
                output.AppendLine("... Comparacion truncada por longitud ...")
                Exit While
            End If
            If oldIndex >= currentLines.Length Then
                output.AppendLine("+ " & candidateLines(newIndex))
                newIndex += 1
            ElseIf newIndex >= candidateLines.Length Then
                output.AppendLine("- " & currentLines(oldIndex))
                oldIndex += 1
            ElseIf String.Equals(currentLines(oldIndex), candidateLines(newIndex), StringComparison.Ordinal) Then
                output.AppendLine("  " & currentLines(oldIndex))
                oldIndex += 1
                newIndex += 1
            Else
                Dim oldOffset As Integer = -1
                Dim newOffset As Integer = -1
                Dim bestCost As Integer = Integer.MaxValue
                For oldStep As Integer = 0 To Math.Min(30, currentLines.Length - oldIndex - 1)
                    For newStep As Integer = 0 To Math.Min(30, candidateLines.Length - newIndex - 1)
                        Dim cost As Integer = oldStep + newStep
                        If cost > 0 AndAlso cost < bestCost AndAlso
                           String.Equals(currentLines(oldIndex + oldStep), candidateLines(newIndex + newStep), StringComparison.Ordinal) Then
                            bestCost = cost
                            oldOffset = oldStep
                            newOffset = newStep
                        End If
                    Next
                Next

                If oldOffset < 0 Then
                    output.AppendLine("- " & currentLines(oldIndex))
                    output.AppendLine("+ " & candidateLines(newIndex))
                    oldIndex += 1
                    newIndex += 1
                Else
                    For index As Integer = 0 To oldOffset - 1
                        output.AppendLine("- " & currentLines(oldIndex + index))
                    Next
                    For index As Integer = 0 To newOffset - 1
                        output.AppendLine("+ " & candidateLines(newIndex + index))
                    Next
                    oldIndex += oldOffset
                    newIndex += newOffset
                End If
            End If
        End While
        Return output.ToString()
    End Function

    Private Shared Function Normalize(value As String) As String
        Return value.Replace(vbCrLf, vbLf).Replace(vbCr, vbLf).TrimEnd(ChrW(10))
    End Function
End Class

Friend NotInheritable Class DatabaseSearchForm
    Inherits Form

    Private ReadOnly locationsGrid As New DataGridView()
    Private ReadOnly chooseButton As New Button()
    Public Property SelectedDatabase As String

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
        locationsGrid.MultiSelect = False
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
        chooseButton.Text = "Usar base seleccionada"
        chooseButton.Width = 190
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
        If locationsGrid.CurrentRow Is Nothing Then Return
        SelectedDatabase = Convert.ToString(locationsGrid.CurrentRow.Cells(0).Value)
        Me.DialogResult = DialogResult.OK
        Me.Close()
    End Sub
End Class
