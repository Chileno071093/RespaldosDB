Imports System
Imports System.IO
Imports System.Xml.Serialization

' Ultimos valores usados en la ventana principal. El password nunca se guarda.
Public NotInheritable Class UserSettings
    Public Property Server As String
    Public Property Database As String
    Public Property UserName As String
    Public Property SourceFolder As String
    Public Property DestinationFolder As String
    Public Property IncludeSubfolders As Boolean
    Public Property Encrypt As Boolean = True
    Public Property TrustServerCertificate As Boolean = True

    Friend Shared ReadOnly DefaultPath As String = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RespaldoObjetosSQL", "configuracion.xml")

    Friend Shared Function Load(filePath As String) As UserSettings
        Try
            If File.Exists(filePath) Then
                Using stream As FileStream = File.OpenRead(filePath)
                    Dim value As UserSettings = TryCast(New XmlSerializer(GetType(UserSettings)).Deserialize(stream), UserSettings)
                    If value IsNot Nothing Then Return value
                End Using
            End If
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is InvalidOperationException
            ' Archivo ilegible o dañado: se usan los valores por defecto.
        End Try
        Return New UserSettings()
    End Function

    Friend Sub Save(filePath As String)
        Directory.CreateDirectory(Path.GetDirectoryName(filePath))
        Dim tempPath As String = filePath & ".tmp"
        Using stream As FileStream = File.Create(tempPath)
            Dim serializer As New XmlSerializer(GetType(UserSettings))
            serializer.Serialize(stream, Me)
        End Using
        If File.Exists(filePath) Then
            File.Replace(tempPath, filePath, Nothing)
        Else
            File.Move(tempPath, filePath)
        End If
    End Sub
End Class
