param(
    [Parameter(Mandatory = $true)]
    [string]$Servidor,

    [Parameter(Mandatory = $true)]
    [string]$BaseDatos,

    [Parameter(Mandatory = $true)]
    [string]$Destino,

    [System.Management.Automation.PSCredential]$Credencial,

    [Alias('UserName')]
    [string]$Usuario,

    [System.Security.SecureString]$Password,

    [string]$Origen = 'C:\Users\Alonso Salinas\OneDrive\Desktop\Respaldos-SPs\Gestor Tiempo Extra\Fase 1\6 Reportes - Falta Liberar\Liberar Fase 1 TE'
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $Origen -PathType Container)) {
    throw "No existe la carpeta de scripts: $Origen"
}

$patron = '^\d+\s+\[(?<esquema>[^\]]+)\]\.\[(?<nombre>[^\]]+)\](?:_Respaldo)?\.sql$'
$objetos = @{}
foreach ($archivo in Get-ChildItem -LiteralPath $Origen -Filter '*.sql' -File -Recurse) {
    $coincidencia = [regex]::Match($archivo.Name, $patron)
    if (-not $coincidencia.Success) {
        throw "No se puede identificar el procedimiento a partir de: $($archivo.FullName)"
    }

    $esquema = $coincidencia.Groups['esquema'].Value
    $nombre = $coincidencia.Groups['nombre'].Value
    if ($nombre -notlike 'P_*') {
        continue
    }
    $clave = "$esquema.$nombre"
    $objetos[$clave] = [pscustomobject]@{ Esquema = $esquema; Nombre = $nombre }
}

if ($objetos.Count -eq 0) {
    throw "No se encontraron archivos .sql en $Origen"
}

$conexionConfig = New-Object System.Data.SqlClient.SqlConnectionStringBuilder
$conexionConfig['Data Source'] = $Servidor
$conexionConfig['Initial Catalog'] = $BaseDatos
$conexionConfig['Application Name'] = 'Respaldo procedimientos Fase 1 TE'
if ($Credencial -and ($Usuario -or $Password)) {
    throw 'Usa -Credencial o bien -Usuario y -Password; no ambos formatos.'
}
if ([bool]$Usuario -ne [bool]$Password) {
    throw 'Debes proporcionar -Usuario y -Password juntos.'
}

if ($Usuario) {
    $Credencial = [System.Management.Automation.PSCredential]::new($Usuario, $Password)
}

if ($Credencial) {
    $conexionConfig['Integrated Security'] = $false
    $claveSegura = $Credencial.Password.Copy()
    $claveSegura.MakeReadOnly()
    $credencialSql = [System.Data.SqlClient.SqlCredential]::new($Credencial.UserName, $claveSegura)
    $conexion = [System.Data.SqlClient.SqlConnection]::new($conexionConfig.ConnectionString, $credencialSql)
}
else {
    $conexionConfig['Integrated Security'] = $true
    $conexion = [System.Data.SqlClient.SqlConnection]::new($conexionConfig.ConnectionString)
}
$definiciones = @{}

try {
    $conexion.Open()
    $comando = $conexion.CreateCommand()
    $comando.CommandText = @'
SELECT s.name AS Esquema, p.name AS Nombre, m.definition AS Definicion,
       m.uses_ansi_nulls AS UsaAnsiNulls,
       m.uses_quoted_identifier AS UsaQuotedIdentifier
FROM sys.procedures AS p
JOIN sys.schemas AS s ON s.schema_id = p.schema_id
JOIN sys.sql_modules AS m ON m.object_id = p.object_id;
'@
    $lector = $comando.ExecuteReader()
    try {
        while ($lector.Read()) {
            $clave = "$($lector.GetString(0)).$($lector.GetString(1))"
            if ($objetos.ContainsKey($clave)) {
                if ($lector.IsDBNull(2)) {
                    throw "El procedimiento $clave esta cifrado o su definicion no es visible."
                }
                $definiciones[$clave] = [pscustomobject]@{
                    Texto = $lector.GetString(2)
                    AnsiNulls = $lector.GetBoolean(3)
                    QuotedIdentifier = $lector.GetBoolean(4)
                }
            }
        }
    }
    finally {
        $lector.Close()
    }
}
finally {
    $conexion.Dispose()
}

$faltantes = @($objetos.Keys | Where-Object { -not $definiciones.ContainsKey($_) } | Sort-Object)
if ($definiciones.Count -eq 0) {
    throw "No se encontraron procedimientos visibles para respaldar en [$BaseDatos]."
}

$marca = Get-Date -Format 'yyyyMMdd_HHmmss'
$carpetaRespaldo = Join-Path $Destino "Respaldo_SP_$marca"
if (Test-Path -LiteralPath $carpetaRespaldo) {
    throw "La carpeta de salida ya existe: $carpetaRespaldo"
}
[void](New-Item -ItemType Directory -Path $carpetaRespaldo -Force)
$codificacion = New-Object System.Text.UTF8Encoding($true)
$baseCitada = '[' + $BaseDatos.Replace(']', ']]') + ']'

foreach ($clave in @($objetos.Keys | Sort-Object)) {
    if (-not $definiciones.ContainsKey($clave)) {
        continue
    }
    $objeto = $objetos[$clave]
    $ruta = Join-Path $carpetaRespaldo ("[{0}].[{1}].sql" -f $objeto.Esquema, $objeto.Nombre)
    $modulo = $definiciones[$clave]
    $ansiNulls = if ($modulo.AnsiNulls) { 'ON' } else { 'OFF' }
    $quotedIdentifier = if ($modulo.QuotedIdentifier) { 'ON' } else { 'OFF' }
    $contenido = "USE $baseCitada`r`nGO`r`nSET ANSI_NULLS $ansiNulls`r`nGO`r`nSET QUOTED_IDENTIFIER $quotedIdentifier`r`nGO`r`n`r`n" + $modulo.Texto.TrimEnd() + "`r`nGO`r`n"
    [System.IO.File]::WriteAllText($ruta, $contenido, $codificacion)
}

if ($faltantes.Count -gt 0) {
    $aviso = "Base de datos: $BaseDatos`r`nNo encontrados o no visibles (sin respaldo):`r`n" + ($faltantes -join "`r`n") + "`r`n"
    [System.IO.File]::WriteAllText((Join-Path $carpetaRespaldo 'Procedimientos_no_encontrados.txt'), $aviso, $codificacion)
    Write-Warning "No se respaldaron $($faltantes.Count) procedimientos: $($faltantes -join ', ')"
}

Write-Host "Respaldo creado: $carpetaRespaldo"
Write-Host "Procedimientos respaldados: $($definiciones.Count)"
