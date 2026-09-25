# Compila la aplicacion y las pruebas y las ejecuta (equivale a "dotnet test" sobre el proyecto de pruebas).
# Uso (desde cualquier carpeta):  powershell -ExecutionPolicy Bypass -File Pruebas\Ejecutar-Pruebas.ps1
# Solo algunas pruebas:           ... -Filtro "Name~Respaldar"
# Las pruebas de integracion necesitan SQL Server LocalDB; si no esta instalado se marcan como "no concluyentes".
param(
    [string]$Filtro
)

$ErrorActionPreference = 'Stop'
$proyecto = Join-Path $PSScriptRoot 'RespaldoProcedimientosDesktop.Tests.vbproj'
$argumentos = @('test', $proyecto)
if ($Filtro) { $argumentos += @('--filter', $Filtro) }
& dotnet @argumentos
exit $LASTEXITCODE
