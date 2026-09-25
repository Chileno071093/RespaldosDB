# Compila la aplicacion y las pruebas con MSBuild de Visual Studio y las ejecuta con vstest.
# Uso (desde cualquier carpeta):  powershell -ExecutionPolicy Bypass -File Pruebas\Ejecutar-Pruebas.ps1
# Las pruebas de integracion necesitan SQL Server LocalDB; si no esta instalado se marcan como "no concluyentes".
param(
    [string]$Filtro
)

$ErrorActionPreference = 'Stop'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) { throw 'No se encontro Visual Studio (vswhere.exe).' }
# Se exige MSBuild para no elegir otras herramientas basadas en Visual Studio (por ejemplo, SSMS).
$instalacion = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath | Select-Object -First 1
if (-not $instalacion) { throw 'No se encontro una instalacion de Visual Studio con MSBuild.' }
$msbuild = Get-ChildItem -Path (Join-Path $instalacion 'MSBuild') -Filter MSBuild.exe -Recurse | Where-Object { $_.FullName -match '\\Bin\\MSBuild\.exe$' } | Select-Object -First 1 -ExpandProperty FullName
$vstest = Join-Path $instalacion 'Common7\IDE\Extensions\TestPlatform\vstest.console.exe'
if (-not $msbuild -or -not (Test-Path -LiteralPath $vstest)) { throw 'No se encontro MSBuild o vstest.console.exe en ' + $instalacion }

$proyecto = Join-Path $PSScriptRoot 'RespaldoProcedimientosDesktop.Tests.vbproj'
& $msbuild $proyecto /restore /t:Build /p:Configuration=Debug /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$dll = Join-Path $PSScriptRoot 'bin\Debug\net48\RespaldoProcedimientosDesktop.Tests.dll'
$argumentos = @($dll)
if ($Filtro) { $argumentos += "/TestCaseFilter:$Filtro" }
& $vstest @argumentos
exit $LASTEXITCODE
