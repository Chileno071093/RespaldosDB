Imports System
Imports System.Collections.Generic
Imports System.Data
Imports System.Data.SqlClient
Imports System.Diagnostics
Imports System.IO
Imports System.Linq
Imports System.Security.Cryptography
Imports System.Text
Imports System.Threading
Imports Microsoft.VisualBasic

Friend NotInheritable Class BackupRequest
    Public Property Server As String
    Public Property Database As String
    Public Property UserName As String
    Public Property Password As String
    Public Property SourceFolder As String
    Public Property DestinationFolder As String
    Public Property IncludeSubfolders As Boolean
End Class

Friend NotInheritable Class CatalogObject
    Public Property SchemaName As String
    Public Property ObjectName As String
    Public Property TypeCode As String
    Public Property Definition As String
    Public Property AnsiNulls As Boolean
    Public Property QuotedIdentifier As Boolean
End Class

Friend NotInheritable Class AnalysisItem
    Public Property Key As String
    Public Property Declaration As DeclaredObject
    Public Property Current As CatalogObject
    Public Property Status As String
    Public Property Detail As String
    Public Property AlsoDeclaredIn As List(Of DeclaredObject)

    Public ReadOnly Property CanBackup As Boolean
        Get
            Return Current IsNot Nothing AndAlso Current.Definition IsNot Nothing AndAlso Status = "Listo"
        End Get
    End Property
End Class

Friend NotInheritable Class BackupAnalysis
    Public Property Items As List(Of AnalysisItem)
    Public Property FilesWithoutObject As List(Of String)
    Public Property DetectedDeclarations As List(Of String)
    Public Property SourceHashes As Dictionary(Of String, String)
End Class

Friend NotInheritable Class BackupResult
    Public Property Folder As String
    Public Property SavedObjects As List(Of String)
    Public Property Skipped As List(Of String)
    Public Property VerifiedFiles As Integer
End Class

Friend NotInheritable Class DatabaseLocation
    Public Property DatabaseName As String
    Public Property Kind As String
    Public Property SchemaName As String
    Public Property ObjectName As String
End Class

Friend NotInheritable Class DatabaseSearchReport
    Public Property Locations As List(Of DatabaseLocation)
    Public Property SkippedDatabases As List(Of String)
    Public Property NamesWithoutVisibleMatch As List(Of String)
    Public Property ScannedDatabases As Integer
    Public Property DatabaseTimings As List(Of String)
End Class

Friend NotInheritable Class BackupService
    Private Sub New()
    End Sub

    Public Shared Function Analyze(request As BackupRequest, cancellation As CancellationToken, progress As IProgress(Of String)) As BackupAnalysis
        cancellation.ThrowIfCancellationRequested()
        If Not Directory.Exists(request.SourceFolder) Then
            Throw New DirectoryNotFoundException("No existe la carpeta de scripts: " & request.SourceFolder)
        End If

        Dim sourceRoot As String = Path.GetFullPath(request.SourceFolder).TrimEnd(Path.DirectorySeparatorChar)
        Dim destinationRoot As String = Path.GetFullPath(request.DestinationFolder).TrimEnd(Path.DirectorySeparatorChar)
        Dim destinationInsideSource As Boolean = destinationRoot.StartsWith(sourceRoot & Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        Dim searchOption As SearchOption = If(request.IncludeSubfolders, SearchOption.AllDirectories, SearchOption.TopDirectoryOnly)
        Dim declared As New Dictionary(Of String, DeclaredObject)(StringComparer.OrdinalIgnoreCase)
        Dim detectedDeclarations As New List(Of String)()
        Dim filesWithoutObject As New List(Of String)()
        Dim sourceHashes As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        Dim duplicates As New Dictionary(Of String, List(Of DeclaredObject))(StringComparer.OrdinalIgnoreCase)

        Dim fileCount As Integer = 0
        For Each filePath As String In Directory.EnumerateFiles(sourceRoot, "*.sql", searchOption)
            cancellation.ThrowIfCancellationRequested()
            If destinationInsideSource AndAlso filePath.StartsWith(destinationRoot & Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) Then Continue For
            If IsInBackupSubfolder(sourceRoot, filePath) Then Continue For

            fileCount += 1
            If progress IsNot Nothing Then progress.Report("Leyendo script " & fileCount.ToString() & ": " & Path.GetFileName(filePath))
            Dim hash As String = Nothing
            Dim script As String = ReadScript(filePath, hash)
            sourceHashes(filePath) = hash
            cancellation.ThrowIfCancellationRequested()
            Dim objects As List(Of DeclaredObject) = SqlObjectParser.Parse(script, filePath)
            If objects.Count = 0 Then filesWithoutObject.Add(filePath)
            For Each item As DeclaredObject In objects
                Dim databasePrefix As String = If(String.IsNullOrEmpty(item.DatabaseName), "", "[" & item.DatabaseName & "].")
                detectedDeclarations.Add(filePath & " -> " & databasePrefix & item.DisplayName())
                Dim key As String = item.Kind & "|" & item.DatabaseName & "|" & item.SchemaName & "|" & item.ObjectName
                Dim first As DeclaredObject = Nothing
                If Not declared.TryGetValue(key, first) Then
                    declared.Add(key, item)
                ElseIf Not String.Equals(first.SourceFile, item.SourceFile, StringComparison.OrdinalIgnoreCase) Then
                    Dim others As List(Of DeclaredObject) = Nothing
                    If Not duplicates.TryGetValue(key, others) Then
                        others = New List(Of DeclaredObject)()
                        duplicates.Add(key, others)
                    End If
                    If Not others.Any(Function(x) String.Equals(x.SourceFile, item.SourceFile, StringComparison.OrdinalIgnoreCase)) Then others.Add(item)
                End If
            Next
        Next

        If declared.Count = 0 Then
            Throw New InvalidOperationException("No se encontraron declaraciones CREATE o ALTER de procedimientos, vistas, triggers o funciones en los archivos .sql.")
        End If

        cancellation.ThrowIfCancellationRequested()
        If progress IsNot Nothing Then progress.Report("Consultando en SQL Server solo los nombres detectados...")
        Dim names As List(Of String) = declared.Values.Where(Function(x) String.IsNullOrEmpty(x.DatabaseName) OrElse String.Equals(x.DatabaseName, request.Database, StringComparison.OrdinalIgnoreCase)).Select(Function(x) x.ObjectName).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        Dim catalog As List(Of CatalogObject) = ReadCatalog(request, names, cancellation, progress)
        Dim rows As New List(Of AnalysisItem)()
        For Each pair As KeyValuePair(Of String, DeclaredObject) In declared.OrderBy(Function(x) x.Value.DisplayName())
            cancellation.ThrowIfCancellationRequested()
            Dim item As DeclaredObject = pair.Value
            Dim others As List(Of DeclaredObject) = Nothing
            If Not duplicates.TryGetValue(pair.Key, others) Then others = New List(Of DeclaredObject)()
            Dim row As New AnalysisItem With {.Key = pair.Key, .Declaration = item, .AlsoDeclaredIn = others}
            If Not String.IsNullOrEmpty(item.DatabaseName) AndAlso
               Not String.Equals(item.DatabaseName, request.Database, StringComparison.OrdinalIgnoreCase) Then
                row.Status = "Otra base"
                row.Detail = "El nombre declarado apunta a [" & item.DatabaseName & "]."
            Else
                Dim candidates As List(Of CatalogObject) = catalog.Where(Function(x) Matches(item, x)).ToList()
                If candidates.Count = 0 Then
                    row.Status = "No visible o inexistente"
                    row.Detail = "Con este usuario no es posible distinguir falta de permisos de ausencia del objeto."
                ElseIf candidates.Count > 1 Then
                    row.Status = "Ambiguo"
                    row.Detail = "Hay varios esquemas con ese nombre; indica el esquema en el script."
                Else
                    row.Current = candidates(0)
                    If row.Current.Definition Is Nothing Then
                        row.Status = "Sin definicion"
                        row.Detail = "El objeto existe, pero la definicion esta cifrada o no es visible."
                    Else
                        row.Status = "Listo"
                        row.Detail = "Definicion actual disponible para respaldo."
                    End If
                End If
            End If
            If others.Count > 0 Then
                row.Detail &= " Declarado tambien en: " & String.Join(", ", others.Select(Function(x) Path.GetFileName(x.SourceFile))) & " (usa Comparar para revisar cada archivo)."
            End If
            rows.Add(row)
        Next

        cancellation.ThrowIfCancellationRequested()
        Return New BackupAnalysis With {
            .Items = rows,
            .FilesWithoutObject = filesWithoutObject,
            .DetectedDeclarations = detectedDeclarations,
            .SourceHashes = sourceHashes
        }
    End Function

    Public Shared Function FindDatabases(request As BackupRequest, selectedObjects As IEnumerable(Of DeclaredObject), cancellation As CancellationToken, progress As IProgress(Of String)) As DatabaseSearchReport
        Dim targets As List(Of DeclaredObject) = selectedObjects.ToList()
        If targets.Count = 0 Then Throw New InvalidOperationException("Selecciona al menos una fila de la vista previa.")
        Dim names As List(Of String) = targets.Select(Function(x) x.ObjectName).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        If names.Count > 1000 Then Throw New InvalidOperationException("Selecciona hasta 1000 nombres por busqueda.")

        cancellation.ThrowIfCancellationRequested()
        If progress IsNot Nothing Then progress.Report("Obteniendo bases de datos accesibles...")
        Dim databases As New List(Of String)()
        Dim locations As New Dictionary(Of String, DatabaseLocation)(StringComparer.OrdinalIgnoreCase)
        Dim skipped As New List(Of String)()
        Dim timings As New List(Of String)()
        Dim scanned As Integer = 0
        Dim placeholders As New List(Of String)()
        For index As Integer = 0 To names.Count - 1
            placeholders.Add("@n" & index.ToString())
        Next
        Dim nameFilter As String = String.Join(",", placeholders)

        ' Una sola conexion para todo el recorrido: cambiar de base con ChangeDatabase evita repetir el login por cada base.
        Dim connection As SqlConnection = Nothing
        Try
            Try
                connection = OpenConnection(request, request.Database, cancellation)
                Using command As SqlCommand = connection.CreateCommand()
                    command.CommandTimeout = 10
                    command.CommandText = "SELECT name FROM sys.databases WHERE state = 0 AND database_id > 4 AND HAS_DBACCESS(name) = 1 ORDER BY name;"
                    Using registration As CancellationTokenRegistration = cancellation.Register(Sub() CancelCommand(command))
                        cancellation.ThrowIfCancellationRequested()
                        Using reader As SqlDataReader = command.ExecuteReader()
                            While reader.Read()
                                cancellation.ThrowIfCancellationRequested()
                                databases.Add(reader.GetString(0))
                            End While
                        End Using
                    End Using
                End Using
            Catch ex As SqlException
                cancellation.ThrowIfCancellationRequested()
                Throw
            End Try

            For databaseIndex As Integer = 0 To databases.Count - 1
                cancellation.ThrowIfCancellationRequested()
                Dim databaseName As String = databases(databaseIndex)
                If progress IsNot Nothing Then progress.Report("Revisando base " & (databaseIndex + 1).ToString() & "/" & databases.Count.ToString() & ": " & databaseName)
                Dim watch As Stopwatch = Stopwatch.StartNew()
                Try
                    If connection Is Nothing OrElse connection.State <> ConnectionState.Open Then
                        If connection IsNot Nothing Then connection.Dispose()
                        connection = Nothing
                        connection = OpenConnection(request, databaseName, cancellation)
                    Else
                        connection.ChangeDatabase(databaseName)
                    End If
                    cancellation.ThrowIfCancellationRequested()
                    Using command As SqlCommand = connection.CreateCommand()
                        command.CommandTimeout = 10
                        command.CommandText = "SELECT s.name, o.name, o.type FROM sys.objects AS o " &
                                              "JOIN sys.schemas AS s ON s.schema_id = o.schema_id " &
                                              "WHERE o.type IN ('P','V','TR','FN','IF','TF') AND o.name IN (" & nameFilter & ") " &
                                              "UNION ALL SELECT CAST(NULL AS nvarchar(128)), t.name, t.type " &
                                              "FROM sys.triggers AS t WHERE t.parent_class = 0 AND t.type = 'TR' AND t.name IN (" & nameFilter & ");"
                        For index As Integer = 0 To names.Count - 1
                            command.Parameters.Add(placeholders(index), SqlDbType.NVarChar, 128).Value = names(index)
                        Next
                        Using registration As CancellationTokenRegistration = cancellation.Register(Sub() CancelCommand(command))
                            cancellation.ThrowIfCancellationRequested()
                            Using reader As SqlDataReader = command.ExecuteReader()
                                While reader.Read()
                                    cancellation.ThrowIfCancellationRequested()
                                    Dim entry As New CatalogObject With {
                                        .SchemaName = If(reader.IsDBNull(0), Nothing, reader.GetString(0)),
                                        .ObjectName = reader.GetString(1),
                                        .TypeCode = reader.GetString(2).Trim()
                                    }
                                    For Each target As DeclaredObject In targets
                                        If Not Matches(target, entry) Then Continue For
                                        Dim key As String = databaseName & "|" & entry.TypeCode & "|" & entry.SchemaName & "|" & entry.ObjectName
                                        If Not locations.ContainsKey(key) Then
                                            locations.Add(key, New DatabaseLocation With {
                                                .DatabaseName = databaseName,
                                                .Kind = KindName(entry.TypeCode),
                                                .SchemaName = entry.SchemaName,
                                                .ObjectName = entry.ObjectName
                                            })
                                        End If
                                    Next
                                End While
                            End Using
                        End Using
                    End Using
                    scanned += 1
                    timings.Add(databaseName & " | " & watch.Elapsed.TotalSeconds.ToString("0.0") & " s | Revisada")
                Catch ex As SqlException
                    cancellation.ThrowIfCancellationRequested()
                    skipped.Add(databaseName & ": " & ex.Message)
                    timings.Add(databaseName & " | " & watch.Elapsed.TotalSeconds.ToString("0.0") & " s | Omitida: " & ex.Message)
                Finally
                    watch.Stop()
                    If progress IsNot Nothing AndAlso Not cancellation.IsCancellationRequested Then progress.Report("Completadas " & (databaseIndex + 1).ToString() & "/" & databases.Count.ToString() & " bases. " & databaseName & ": " & watch.Elapsed.TotalSeconds.ToString("0.0") & " s")
                End Try
            Next
        Finally
            If connection IsNot Nothing Then connection.Dispose()
        End Try

        Dim unmatched As New List(Of String)()
        For Each target As DeclaredObject In targets
            Dim found As Boolean = locations.Values.Any(Function(x) String.Equals(x.Kind, target.Kind, StringComparison.OrdinalIgnoreCase) AndAlso
                                                                String.Equals(x.ObjectName, target.ObjectName, StringComparison.OrdinalIgnoreCase) AndAlso
                                                                (String.IsNullOrEmpty(target.SchemaName) OrElse String.Equals(x.SchemaName, target.SchemaName, StringComparison.OrdinalIgnoreCase)))
            If Not found AndAlso Not unmatched.Contains(target.DisplayName()) Then unmatched.Add(target.DisplayName())
        Next

        cancellation.ThrowIfCancellationRequested()
        Return New DatabaseSearchReport With {
            .Locations = locations.Values.OrderBy(Function(x) x.ObjectName).ThenBy(Function(x) x.DatabaseName).ToList(),
            .SkippedDatabases = skipped,
            .NamesWithoutVisibleMatch = unmatched,
            .ScannedDatabases = scanned,
            .DatabaseTimings = timings
        }
    End Function

    Private Shared Function OpenConnection(request As BackupRequest, databaseName As String, cancellation As CancellationToken) As SqlConnection
        Dim connection As New SqlConnection(ConnectionStringFor(request, databaseName))
        Try
            connection.Open()
            cancellation.ThrowIfCancellationRequested()
            Return connection
        Catch
            connection.Dispose()
            Throw
        End Try
    End Function

    Private Shared Sub CancelCommand(command As SqlCommand)
        Try
            command.Cancel()
        Catch ex As InvalidOperationException
        End Try
    End Sub

    Public Shared Function Save(request As BackupRequest, analysis As BackupAnalysis, selectedKeys As IEnumerable(Of String), cancellation As CancellationToken, progress As IProgress(Of String)) As BackupResult
        cancellation.ThrowIfCancellationRequested()
        Dim selectedSet As New HashSet(Of String)(selectedKeys, StringComparer.OrdinalIgnoreCase)
        Dim selected As List(Of AnalysisItem) = analysis.Items.Where(Function(x) selectedSet.Contains(x.Key) AndAlso x.CanBackup).ToList()
        If selected.Count = 0 Then Throw New InvalidOperationException("Marca al menos un objeto con estado Listo.")
        If selected.Count <> selectedSet.Count Then Throw New InvalidOperationException("La seleccion contiene objetos no disponibles; vuelve a analizar.")

        If progress IsNot Nothing Then progress.Report("Verificando que los scripts de origen no cambiaron...")
        Dim selectedSources As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        For Each row As AnalysisItem In selected
            selectedSources.Add(row.Declaration.SourceFile)
            If row.AlsoDeclaredIn IsNot Nothing Then
                For Each other As DeclaredObject In row.AlsoDeclaredIn
                    selectedSources.Add(other.SourceFile)
                Next
            End If
        Next
        For Each sourceFile As String In selectedSources
            cancellation.ThrowIfCancellationRequested()
            Dim expectedHash As String = Nothing
            If Not analysis.SourceHashes.TryGetValue(sourceFile, expectedHash) OrElse
               Not File.Exists(sourceFile) OrElse
               Not String.Equals(FileHash(sourceFile), expectedHash, StringComparison.Ordinal) Then
                Throw New InvalidOperationException("El archivo " & sourceFile & " cambio desde la vista previa. Vuelve a analizar.")
            End If
        Next

        If progress IsNot Nothing Then progress.Report("Verificando las definiciones seleccionadas en SQL Server...")
        Dim currentCatalog As List(Of CatalogObject) = ReadCatalog(request, selected.Select(Function(x) x.Current.ObjectName), cancellation, progress)
        For Each row As AnalysisItem In selected
            cancellation.ThrowIfCancellationRequested()
            Dim matchesNow As List(Of CatalogObject) = currentCatalog.Where(Function(x) ExactMatch(row.Current, x)).ToList()
            If matchesNow.Count <> 1 OrElse matchesNow(0).Definition Is Nothing Then
                Throw New InvalidOperationException("El objeto " & row.Declaration.DisplayName() & " ya no esta disponible. Vuelve a analizar.")
            End If
            Dim now As CatalogObject = matchesNow(0)
            If Not String.Equals(row.Current.Definition, now.Definition, StringComparison.Ordinal) OrElse
               row.Current.AnsiNulls <> now.AnsiNulls OrElse row.Current.QuotedIdentifier <> now.QuotedIdentifier Then
                Throw New InvalidOperationException("La definicion de " & row.Declaration.DisplayName() & " cambio desde la vista previa. Vuelve a analizar.")
            End If
        Next

        Dim destinationRoot As String = Path.GetFullPath(request.DestinationFolder)
        Dim finalFolder As String = Path.Combine(destinationRoot, "Respaldo_Objetos_" & DateTime.Now.ToString("yyyyMMdd_HHmmss"))
        If Directory.Exists(finalFolder) Then Throw New IOException("La carpeta de salida ya existe: " & finalFolder)
        Directory.CreateDirectory(destinationRoot)
        Dim stagingFolder As String = Path.Combine(destinationRoot, ".respaldo_temp_" & Guid.NewGuid().ToString("N"))
        Dim utf16Le As New UnicodeEncoding(False, True)
        Dim quotedDatabase As String = "[" & request.Database.Replace("]", "]]") & "]"
        Dim savedObjects As New List(Of String)()
        Dim verificationLines As New List(Of String)()
        Dim skipped As List(Of String) = analysis.Items.Where(Function(x) Not selectedSet.Contains(x.Key)).Select(Function(x) x.Declaration.DisplayName() & " - " & x.Status).ToList()

        Try
            cancellation.ThrowIfCancellationRequested()
            Directory.CreateDirectory(stagingFolder)
            Dim savedCount As Integer = 0
            For Each row As AnalysisItem In selected.OrderBy(Function(x) x.Declaration.DisplayName())
                cancellation.ThrowIfCancellationRequested()
                If progress IsNot Nothing Then progress.Report("Guardando objeto " & (savedCount + 1).ToString() & "/" & selected.Count.ToString() & ": " & row.Declaration.DisplayName())
                Dim entry As CatalogObject = row.Current
                Dim kind As String = KindName(entry.TypeCode)
                Dim folder As String = Path.Combine(stagingFolder, FolderName(kind))
                Directory.CreateDirectory(folder)
                Dim objectLabel As String = If(String.IsNullOrEmpty(entry.SchemaName),
                                               "[" & entry.ObjectName & "]",
                                               "[" & entry.SchemaName & "].[" & entry.ObjectName & "]")
                Dim fileName As String = SafeFileName(objectLabel & "_" & request.Database & ".sql")
                Dim filePath As String = Path.Combine(folder, fileName)
                If File.Exists(filePath) Then Throw New IOException("Nombre de archivo repetido para " & kind & " " & objectLabel)

                Dim ansiValue As String = If(entry.AnsiNulls, "ON", "OFF")
                Dim quotedValue As String = If(entry.QuotedIdentifier, "ON", "OFF")
                Dim header As String = "USE " & quotedDatabase & vbCrLf & "GO" & vbCrLf &
                                       "SET ANSI_NULLS " & ansiValue & vbCrLf & "GO" & vbCrLf &
                                       "SET QUOTED_IDENTIFIER " & quotedValue & vbCrLf & "GO" & vbCrLf & vbCrLf
                Dim definition As String = NormalizeLines(entry.Definition).TrimEnd()
                Dim content As String = header & definition & vbCrLf & "GO" & vbCrLf
                Dim hash As String = WriteVerified(filePath, content, utf16Le)
                verificationLines.Add(kind & " " & objectLabel & " | " & Path.Combine(FolderName(kind), fileName) & " | SHA256 " & hash)
                savedObjects.Add(kind & " " & objectLabel)
                savedCount += 1
            Next

            cancellation.ThrowIfCancellationRequested()
            If progress IsNot Nothing Then progress.Report("Escribiendo y verificando reportes...")
            If skipped.Count > 0 Then
                WriteVerified(Path.Combine(stagingFolder, "Objetos_no_respaldados.txt"),
                              String.Join(vbCrLf, skipped) & vbCrLf, utf16Le)
            End If
            WriteVerified(Path.Combine(stagingFolder, "Declaraciones_detectadas.txt"),
                          String.Join(vbCrLf, analysis.DetectedDeclarations) & vbCrLf, utf16Le)
            If analysis.FilesWithoutObject.Count > 0 Then
                WriteVerified(Path.Combine(stagingFolder, "Archivos_sin_objetos.txt"),
                              String.Join(vbCrLf, analysis.FilesWithoutObject) & vbCrLf, utf16Le)
            End If

            Dim typeCounts As String = String.Join(vbCrLf, selected.GroupBy(Function(x) x.Declaration.Kind).OrderBy(Function(x) x.Key).Select(Function(x) x.Key & ": " & x.Count().ToString()))
            Dim verificationReport As String = "Base: " & request.Database & vbCrLf &
                                               "Objetos seleccionados: " & selected.Count.ToString() & vbCrLf &
                                               "Archivos SQL verificados: " & verificationLines.Count.ToString() & vbCrLf &
                                               typeCounts & vbCrLf & vbCrLf &
                                               String.Join(vbCrLf, verificationLines) & vbCrLf
            WriteVerified(Path.Combine(stagingFolder, "Verificacion.txt"), verificationReport, utf16Le)
            File.WriteAllText(Path.Combine(stagingFolder, ".editorconfig"),
                              "root = true" & vbCrLf & vbCrLf &
                              "[*.sql]" & vbCrLf &
                              "charset = utf-16le" & vbCrLf &
                              "end_of_line = crlf" & vbCrLf &
                              "indent_style = tab" & vbCrLf,
                              New UTF8Encoding(False))

            cancellation.ThrowIfCancellationRequested()
            Directory.Move(stagingFolder, finalFolder)
        Finally
            If Directory.Exists(stagingFolder) Then Directory.Delete(stagingFolder, True)
        End Try

        Return New BackupResult With {
            .Folder = finalFolder,
            .SavedObjects = savedObjects,
            .Skipped = skipped,
            .VerifiedFiles = verificationLines.Count
        }
    End Function

    Private Shared Function WriteVerified(filePath As String, content As String, encoding As Encoding) As String
        Dim body As Byte() = encoding.GetBytes(content)
        Dim prefix As Byte() = encoding.GetPreamble()
        Dim expected(prefix.Length + body.Length - 1) As Byte
        Buffer.BlockCopy(prefix, 0, expected, 0, prefix.Length)
        Buffer.BlockCopy(body, 0, expected, prefix.Length, body.Length)
        File.WriteAllBytes(filePath, expected)
        Dim actual As Byte() = File.ReadAllBytes(filePath)
        If Not expected.SequenceEqual(actual) Then Throw New IOException("La verificacion del archivo fallo: " & filePath)
        Using sha As SHA256 = SHA256.Create()
            Return BitConverter.ToString(sha.ComputeHash(actual)).Replace("-", "")
        End Using
    End Function

    Private Shared ReadOnly StrictUtf8 As New UTF8Encoding(False, True)

    Private Shared Function ReadScript(filePath As String, ByRef hash As String) As String
        Dim bytes As Byte() = File.ReadAllBytes(filePath)
        Using sha As SHA256 = SHA256.Create()
            hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "")
        End Using
        Return DecodeScript(bytes)
    End Function

    ' Respeta el BOM si existe; sin BOM intenta UTF-8 estricto y, si no es valido, usa la pagina ANSI del sistema (p. ej. Windows-1252).
    Friend Shared Function DecodeScript(bytes As Byte()) As String
        If bytes.Length >= 3 AndAlso bytes(0) = &HEF AndAlso bytes(1) = &HBB AndAlso bytes(2) = &HBF Then
            Return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3)
        End If
        If bytes.Length >= 4 AndAlso bytes(0) = &HFF AndAlso bytes(1) = &HFE AndAlso bytes(2) = 0 AndAlso bytes(3) = 0 Then
            Return Encoding.UTF32.GetString(bytes, 4, bytes.Length - 4)
        End If
        If bytes.Length >= 2 AndAlso bytes(0) = &HFF AndAlso bytes(1) = &HFE Then
            Return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2)
        End If
        If bytes.Length >= 2 AndAlso bytes(0) = &HFE AndAlso bytes(1) = &HFF Then
            Return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2)
        End If
        Try
            Return StrictUtf8.GetString(bytes)
        Catch ex As DecoderFallbackException
            Return Encoding.Default.GetString(bytes)
        End Try
    End Function

    Private Shared Function FileHash(filePath As String) As String
        Using sha As SHA256 = SHA256.Create()
            Using stream As FileStream = File.OpenRead(filePath)
                Return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "")
            End Using
        End Using
    End Function

    Private Shared Function NormalizeLines(value As String) As String
        Return value.Replace(vbCrLf, vbLf).Replace(vbCr, vbLf).Replace(vbLf, vbCrLf)
    End Function

    Private Shared Function ReadCatalog(request As BackupRequest, objectNames As IEnumerable(Of String), cancellation As CancellationToken, progress As IProgress(Of String)) As List(Of CatalogObject)
        Dim result As New List(Of CatalogObject)()
        Dim names As List(Of String) = objectNames.Where(Function(x) Not String.IsNullOrEmpty(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        If names.Count = 0 Then Return result
        cancellation.ThrowIfCancellationRequested()
        Try
            Using connection As New SqlConnection(ConnectionStringFor(request, request.Database))
                connection.Open()
                cancellation.ThrowIfCancellationRequested()
                Const batchSize As Integer = 500
                For offset As Integer = 0 To names.Count - 1 Step batchSize
                    cancellation.ThrowIfCancellationRequested()
                    Dim batch As List(Of String) = names.Skip(offset).Take(batchSize).ToList()
                    If progress IsNot Nothing Then progress.Report("Consultando objetos " & (offset + 1).ToString() & "-" & (offset + batch.Count).ToString() & "/" & names.Count.ToString() & " en SQL Server...")
                    Using command As SqlCommand = connection.CreateCommand()
                        command.CommandTimeout = 30
                        Dim placeholders As New List(Of String)()
                        For index As Integer = 0 To batch.Count - 1
                            Dim parameterName As String = "@n" & index.ToString()
                            placeholders.Add(parameterName)
                            command.Parameters.Add(parameterName, SqlDbType.NVarChar, 128).Value = batch(index)
                        Next
                        Dim nameFilter As String = String.Join(",", placeholders)
                        command.CommandText = "SELECT s.name, o.name, o.type, m.definition, m.uses_ansi_nulls, m.uses_quoted_identifier " &
                                              "FROM sys.objects AS o " &
                                              "JOIN sys.schemas AS s ON s.schema_id = o.schema_id " &
                                              "JOIN sys.sql_modules AS m ON m.object_id = o.object_id " &
                                              "WHERE o.type IN ('P','V','TR','FN','IF','TF') AND o.name IN (" & nameFilter & ") " &
                                              "UNION ALL " &
                                              "SELECT CAST(NULL AS nvarchar(128)), t.name, t.type, m.definition, m.uses_ansi_nulls, m.uses_quoted_identifier " &
                                              "FROM sys.triggers AS t " &
                                              "JOIN sys.sql_modules AS m ON m.object_id = t.object_id " &
                                              "WHERE t.parent_class = 0 AND t.name IN (" & nameFilter & ");"
                        Using registration As CancellationTokenRegistration = cancellation.Register(Sub() CancelCommand(command))
                            cancellation.ThrowIfCancellationRequested()
                            Using reader As SqlDataReader = command.ExecuteReader()
                                While reader.Read()
                                    cancellation.ThrowIfCancellationRequested()
                                    result.Add(New CatalogObject With {
                                        .SchemaName = If(reader.IsDBNull(0), Nothing, reader.GetString(0)),
                                        .ObjectName = reader.GetString(1),
                                        .TypeCode = reader.GetString(2).Trim(),
                                        .Definition = If(reader.IsDBNull(3), Nothing, reader.GetString(3)),
                                        .AnsiNulls = reader.GetBoolean(4),
                                        .QuotedIdentifier = reader.GetBoolean(5)
                                    })
                                End While
                            End Using
                        End Using
                    End Using
                Next
            End Using
        Catch ex As SqlException
            cancellation.ThrowIfCancellationRequested()
            Throw
        End Try
        cancellation.ThrowIfCancellationRequested()
        Return result
    End Function

    Private Shared Function ConnectionStringFor(request As BackupRequest, databaseName As String) As String
        Dim builder As New SqlConnectionStringBuilder With {
            .DataSource = request.Server,
            .InitialCatalog = databaseName,
            .IntegratedSecurity = False,
            .UserID = request.UserName,
            .Password = request.Password,
            .PersistSecurityInfo = False,
            .ConnectTimeout = 5,
            .ApplicationName = "Respaldo objetos SQL TE"
        }
        Return builder.ConnectionString
    End Function

    Private Shared Function Matches(item As DeclaredObject, entry As CatalogObject) As Boolean
        If Not String.Equals(item.Kind, KindName(entry.TypeCode), StringComparison.OrdinalIgnoreCase) Then Return False
        If Not String.Equals(item.ObjectName, entry.ObjectName, StringComparison.OrdinalIgnoreCase) Then Return False
        If Not String.IsNullOrEmpty(item.SchemaName) AndAlso
           Not String.Equals(item.SchemaName, entry.SchemaName, StringComparison.OrdinalIgnoreCase) Then Return False
        Return True
    End Function

    Private Shared Function ExactMatch(left As CatalogObject, right As CatalogObject) As Boolean
        Return String.Equals(left.TypeCode, right.TypeCode, StringComparison.OrdinalIgnoreCase) AndAlso
               String.Equals(left.SchemaName, right.SchemaName, StringComparison.OrdinalIgnoreCase) AndAlso
               String.Equals(left.ObjectName, right.ObjectName, StringComparison.OrdinalIgnoreCase)
    End Function

    Private Shared Function KindName(typeCode As String) As String
        Select Case typeCode
            Case "P" : Return "Procedimiento"
            Case "V" : Return "Vista"
            Case "TR" : Return "Trigger"
            Case "FN", "IF", "TF" : Return "Funcion"
            Case Else : Return "Desconocido"
        End Select
    End Function

    Private Shared Function FolderName(kind As String) As String
        Select Case kind
            Case "Procedimiento" : Return "Procedimientos"
            Case "Vista" : Return "Vistas"
            Case "Trigger" : Return "Triggers"
            Case Else : Return "Funciones"
        End Select
    End Function

    Private Shared Function SafeFileName(value As String) As String
        Dim invalid As Char() = Path.GetInvalidFileNameChars()
        Dim result As New StringBuilder(value.Length)
        For Each character As Char In value
            result.Append(If(invalid.Contains(character), "_"c, character))
        Next
        Return result.ToString()
    End Function

    Private Shared Function IsInBackupSubfolder(sourceRoot As String, filePath As String) As Boolean
        Dim relativePath As String = filePath.Substring(sourceRoot.Length).TrimStart(Path.DirectorySeparatorChar)
        Dim segments As String() = relativePath.Split(Path.DirectorySeparatorChar)
        For index As Integer = 0 To segments.Length - 2
            Dim segment As String = segments(index)
            If String.Equals(segment, "Respaldos", StringComparison.OrdinalIgnoreCase) OrElse
               segment.StartsWith("Respaldo_SP_", StringComparison.OrdinalIgnoreCase) OrElse
               segment.StartsWith("Respaldo_Objetos_", StringComparison.OrdinalIgnoreCase) OrElse
               segment.StartsWith(".respaldo_temp_", StringComparison.OrdinalIgnoreCase) Then Return True
        Next
        Return False
    End Function
End Class
