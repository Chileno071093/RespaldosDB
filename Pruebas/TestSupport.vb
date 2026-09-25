Imports System.IO
Imports System.Reflection
Imports System.Security
Imports System.Text.RegularExpressions
Imports Microsoft.Data.SqlClient
Imports Microsoft.VisualStudio.TestTools.UnitTesting

Friend Module TestSupport
    Friend Const LocalDbServer As String = "(localdb)\MSSQLLocalDB"
    Private ReadOnly InstanceFlags As BindingFlags = BindingFlags.Instance Or BindingFlags.NonPublic Or BindingFlags.Public
    Private ReadOnly BatchSeparator As New Regex("^\s*GO\s*$", RegexOptions.Multiline Or RegexOptions.IgnoreCase)

    Friend Function Secure(value As String) As SecureString
        Dim result As New SecureString()
        For Each character As Char In value
            result.AppendChar(character)
        Next
        result.MakeReadOnly()
        Return result
    End Function

    Friend Function NewTempFolder(prefix As String) As String
        Dim folder As String = Path.Combine(Path.GetTempPath(), prefix & "_" & Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(folder)
        Return folder
    End Function

    Friend Sub TryDeleteFolder(folder As String)
        Try
            If folder IsNot Nothing AndAlso Directory.Exists(folder) Then Directory.Delete(folder, True)
        Catch ex As IOException
        Catch ex As UnauthorizedAccessException
        End Try
    End Sub

    Friend Function GetField(Of T)(target As Object, name As String) As T
        Dim field As FieldInfo = target.GetType().GetField(name, InstanceFlags)
        Assert.IsNotNull(field, "No existe el campo " & name)
        Return DirectCast(field.GetValue(target), T)
    End Function

    ' Las pruebas no deben tocar la configuracion real del usuario.
    Friend Function UseTemporarySettingsFile() As String
        Dim settingsPath As String = Path.Combine(Path.GetTempPath(), "RespaldoPruebas_" & Guid.NewGuid().ToString("N") & ".xml")
        GetType(UserSettings).GetField("DefaultPath", BindingFlags.Static Or BindingFlags.NonPublic Or BindingFlags.Public).SetValue(Nothing, settingsPath)
        Return settingsPath
    End Function

    ' Separa un script en lotes por las lineas GO, como SSMS.
    Friend Function SplitBatches(script As String) As List(Of String)
        Return BatchSeparator.Split(script).Select(Function(x) x.Trim()).Where(Function(x) x.Length > 0).ToList()
    End Function
End Module

' Crea bases de prueba en LocalDB y un login SQL propio, para ejercer el mismo camino (SqlCredential) que en produccion.
Friend NotInheritable Class LocalDbFixture
    Implements IDisposable

    Private ReadOnly password As String = "Pr#" & Guid.NewGuid().ToString("N")
    Public ReadOnly Property Login As String = "respaldo_pruebas_" & Guid.NewGuid().ToString("N").Substring(0, 8)
    Public ReadOnly Property Databases As New List(Of String)()

    Private Sub New()
    End Sub

    ' Devuelve Nothing si LocalDB no esta disponible en el equipo.
    Public Shared Function TryCreate(databaseCount As Integer) As LocalDbFixture
        Try
            Using connection As New SqlConnection(AdminConnectionString("master"))
                connection.Open()
            End Using
        Catch ex As SqlException
            Return Nothing
        End Try
        Dim fixture As New LocalDbFixture()
        Dim suffix As String = Guid.NewGuid().ToString("N").Substring(0, 6)
        fixture.Exec("master", "CREATE LOGIN [" & fixture.Login & "] WITH PASSWORD = N'" & fixture.password & "', CHECK_POLICY = OFF;")
        For index As Integer = 1 To databaseCount
            Dim name As String = "RespaldoPrueba_" & suffix & "_" & index.ToString()
            fixture.Exec("master", "CREATE DATABASE [" & name & "];")
            fixture.Exec(name, "CREATE USER [" & fixture.Login & "] FOR LOGIN [" & fixture.Login & "]; ALTER ROLE db_owner ADD MEMBER [" & fixture.Login & "];")
            fixture.Databases.Add(name)
        Next
        Return fixture
    End Function

    Public Shared Function AdminConnectionString(database As String) As String
        Return New SqlConnectionStringBuilder With {
            .DataSource = LocalDbServer, .InitialCatalog = database, .IntegratedSecurity = True,
            .TrustServerCertificate = True, .ConnectTimeout = 30
        }.ConnectionString
    End Function

    Public Sub Exec(database As String, sql As String)
        Using connection As New SqlConnection(AdminConnectionString(database))
            connection.Open()
            Using command As SqlCommand = connection.CreateCommand()
                command.CommandText = sql
                command.ExecuteNonQuery()
            End Using
        End Using
    End Sub

    Public Function Scalar(database As String, sql As String) As Object
        Using connection As New SqlConnection(AdminConnectionString(database))
            connection.Open()
            Using command As SqlCommand = connection.CreateCommand()
                command.CommandText = sql
                Return command.ExecuteScalar()
            End Using
        End Using
    End Function

    ' Ejecuta un script con lotes GO (por ejemplo, el script de reversion) como lo haria SSMS.
    Public Sub ExecScript(database As String, script As String)
        Using connection As New SqlConnection(AdminConnectionString(database))
            connection.Open()
            For Each batch As String In SplitBatches(script)
                Using command As SqlCommand = connection.CreateCommand()
                    command.CommandText = batch
                    command.ExecuteNonQuery()
                End Using
            Next
        End Using
    End Sub

    Public Function Definition(database As String, objectName As String) As String
        Return CStr(Scalar(database, "SELECT definition FROM sys.sql_modules WHERE object_id = OBJECT_ID(N'" & objectName.Replace("'", "''") & "')"))
    End Function

    Public Function NewRequest(databases As IEnumerable(Of String), sourceFolder As String, destinationFolder As String) As BackupRequest
        Return New BackupRequest With {
            .Server = LocalDbServer,
            .Databases = databases.ToList(),
            .UserName = Login,
            .Password = Secure(password),
            .Encrypt = True,
            .TrustServerCertificate = True,
            .SourceFolder = sourceFolder,
            .DestinationFolder = destinationFolder,
            .IncludeSubfolders = True
        }
    End Function

    Public ReadOnly Property PasswordForUi As String
        Get
            Return password
        End Get
    End Property

    Public Sub Dispose() Implements IDisposable.Dispose
        SqlConnection.ClearAllPools()
        For Each name As String In Databases
            Try
                Exec("master", "IF DB_ID(N'" & name & "') IS NOT NULL BEGIN ALTER DATABASE [" & name & "] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [" & name & "] END;")
            Catch ex As SqlException
            End Try
        Next
        Try
            Exec("master", "IF SUSER_ID(N'" & Login & "') IS NOT NULL DROP LOGIN [" & Login & "];")
        Catch ex As SqlException
        End Try
    End Sub
End Class
