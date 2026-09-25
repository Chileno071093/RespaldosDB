Imports System
Imports System.Collections.Generic
Imports System.Drawing
Imports System.Linq
Imports System.Windows.Forms

' Lista con casillas para elegir una o varias bases del servidor, con filtro por nombre.
Friend NotInheritable Class DatabasePickerForm
    Inherits Form

    Private ReadOnly allNames As List(Of String)
    Private ReadOnly checkedNames As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
    Private ReadOnly filterBox As New TextBox()
    Private ReadOnly databaseList As New CheckedListBox()
    Private ReadOnly countLabel As New Label()
    Private ReadOnly okButton As New Button()
    Private rebuilding As Boolean

    Public ReadOnly Property SelectedDatabases As List(Of String)
        Get
            Return allNames.Where(Function(x) checkedNames.Contains(x)).ToList()
        End Get
    End Property

    Public Sub New(availableNames As IEnumerable(Of String), currentSelection As IEnumerable(Of String))
        Me.Text = "Elegir bases de datos"
        Me.StartPosition = FormStartPosition.CenterParent
        Me.Size = New Size(520, 600)
        Me.MinimumSize = New Size(420, 420)
        Me.MinimizeBox = False
        Me.ShowInTaskbar = False

        allNames = availableNames.ToList()
        ' Las bases escritas a mano que el servidor no devolvio se muestran al final para poder desmarcarlas.
        For Each name As String In currentSelection
            If Not allNames.Contains(name, StringComparer.OrdinalIgnoreCase) Then allNames.Add(name)
            checkedNames.Add(allNames.First(Function(x) String.Equals(x, name, StringComparison.OrdinalIgnoreCase)))
        Next

        Dim root As New TableLayoutPanel With {.Dock = DockStyle.Fill, .Padding = New Padding(12), .ColumnCount = 1, .RowCount = 5}
        root.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100.0F))
        root.RowStyles.Add(New RowStyle(SizeType.Absolute, 34.0F))
        root.RowStyles.Add(New RowStyle(SizeType.Absolute, 40.0F))
        root.RowStyles.Add(New RowStyle(SizeType.Percent, 100.0F))
        root.RowStyles.Add(New RowStyle(SizeType.Absolute, 28.0F))
        root.RowStyles.Add(New RowStyle(SizeType.Absolute, 44.0F))

        Dim filterPanel As New TableLayoutPanel With {.Dock = DockStyle.Fill, .ColumnCount = 2, .RowCount = 1, .Margin = New Padding(0)}
        filterPanel.ColumnStyles.Add(New ColumnStyle(SizeType.Absolute, 50.0F))
        filterPanel.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100.0F))
        filterPanel.Controls.Add(New Label With {.Text = "Filtrar", .AutoSize = True, .Anchor = AnchorStyles.Left}, 0, 0)
        filterBox.Dock = DockStyle.Fill
        filterBox.Margin = New Padding(3, 5, 3, 3)
        AddHandler filterBox.TextChanged, Sub(sender, e) RebuildList()
        filterPanel.Controls.Add(filterBox, 1, 0)
        root.Controls.Add(filterPanel, 0, 0)

        Dim bulkActions As New FlowLayoutPanel With {.Dock = DockStyle.Fill, .WrapContents = False, .Margin = New Padding(0)}
        Dim checkVisible As New Button With {.Text = "Marcar visibles", .Width = 130, .Height = 30}
        Dim uncheckVisible As New Button With {.Text = "Desmarcar visibles", .Width = 140, .Height = 30}
        AddHandler checkVisible.Click, Sub(sender, e) SetVisibleChecked(True)
        AddHandler uncheckVisible.Click, Sub(sender, e) SetVisibleChecked(False)
        bulkActions.Controls.Add(checkVisible)
        bulkActions.Controls.Add(uncheckVisible)
        root.Controls.Add(bulkActions, 0, 1)

        databaseList.Dock = DockStyle.Fill
        databaseList.CheckOnClick = True
        databaseList.IntegralHeight = False
        AddHandler databaseList.ItemCheck, AddressOf ListItemCheck
        root.Controls.Add(databaseList, 0, 2)

        countLabel.Dock = DockStyle.Fill
        countLabel.TextAlign = ContentAlignment.MiddleLeft
        root.Controls.Add(countLabel, 0, 3)

        Dim actions As New FlowLayoutPanel With {.Dock = DockStyle.Fill, .FlowDirection = FlowDirection.RightToLeft, .Margin = New Padding(0)}
        Dim cancel As New Button With {.Text = "Cancelar", .Width = 100, .Height = 32, .DialogResult = DialogResult.Cancel}
        okButton.Text = "Aceptar"
        okButton.Width = 100
        okButton.Height = 32
        okButton.DialogResult = DialogResult.OK
        actions.Controls.Add(cancel)
        actions.Controls.Add(okButton)
        root.Controls.Add(actions, 0, 4)

        Me.Controls.Add(root)
        Me.AcceptButton = okButton
        Me.CancelButton = cancel
        RebuildList()
    End Sub

    Private Sub RebuildList()
        Dim filter As String = filterBox.Text.Trim()
        rebuilding = True
        Try
            databaseList.BeginUpdate()
            databaseList.Items.Clear()
            For Each name As String In allNames
                If filter.Length > 0 AndAlso name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 Then Continue For
                databaseList.Items.Add(name, checkedNames.Contains(name))
            Next
            databaseList.EndUpdate()
        Finally
            rebuilding = False
        End Try
        UpdateCount()
    End Sub

    Private Sub ListItemCheck(sender As Object, e As ItemCheckEventArgs)
        If rebuilding Then Return
        Dim name As String = CStr(databaseList.Items(e.Index))
        If e.NewValue = CheckState.Checked Then checkedNames.Add(name) Else checkedNames.Remove(name)
        UpdateCount()
    End Sub

    Private Sub SetVisibleChecked(value As Boolean)
        For index As Integer = 0 To databaseList.Items.Count - 1
            databaseList.SetItemChecked(index, value)
        Next
    End Sub

    Private Sub UpdateCount()
        countLabel.Text = checkedNames.Count.ToString() & " de " & allNames.Count.ToString() & " base(s) seleccionada(s)" &
                          If(databaseList.Items.Count < allNames.Count, " | mostrando " & databaseList.Items.Count.ToString(), "")
        okButton.Enabled = checkedNames.Count > 0
    End Sub
End Class
