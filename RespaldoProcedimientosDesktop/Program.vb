Imports System
Imports System.Runtime.CompilerServices
Imports System.Windows.Forms

<Assembly: InternalsVisibleTo("RespaldoProcedimientosDesktop.Tests")>

Friend Module Program
    <STAThread>
    Public Sub Main()
        Application.EnableVisualStyles()
        Application.SetCompatibleTextRenderingDefault(False)
        Application.Run(New MainForm())
    End Sub
End Module
