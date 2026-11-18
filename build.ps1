[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',
    [switch]$SelfContained,
    [switch]$NoZip,
    [switch]$SkipTests,
    [switch]$SkipIcon
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'src\Selbar\Selbar.csproj'
$testProject = Join-Path $root 'tools\SelfTest\SelfTest.csproj'
$icon = Join-Path $root 'src\Selbar\Assets\app.ico'
$iss = Join-Path $root 'tools\installer.iss'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "Falta el SDK de .NET 8. Instalalo con: winget install Microsoft.DotNet.SDK.8"
}

# El icono se genera una vez y se queda en el repo.
if (-not $SkipIcon -and -not (Test-Path $icon)) {
    & (Join-Path $root 'tools\make-icon.ps1')
}
# Sin icono, `dotnet publish` falla más tarde con un error que no lo menciona.
if (-not (Test-Path $icon)) {
    throw "Falta el icono $icon. Genéralo con tools\make-icon.ps1 o quita -SkipIcon."
}

$versionMatch = Select-String -Path $project -Pattern '<Version>([^<]+)</Version>' | Select-Object -First 1
if (-not $versionMatch) { throw "No se encontró <Version> en $project." }
$version = $versionMatch.Matches[0].Groups[1].Value
if ([string]::IsNullOrWhiteSpace($version)) { throw "No se encontró <Version> en $project." }
Write-Host "Selbar $version - $Configuration ($Runtime)" -ForegroundColor Cyan

# Versión del instalador, leída con cuidado: si el patrón no aparece,
# .Matches[0] lanzaba «no se puede llamar a un método en una expresión nula»
# y el build moría con un error que no tenía nada que ver.
$issMatch = Select-String -Path $iss -Pattern '#define MyVersion "([^"]+)"' | Select-Object -First 1
$issVersion = if ($issMatch) { $issMatch.Matches[0].Groups[1].Value } else { '' }
if (-not $issVersion) {
    Write-Warning "tools/installer.iss no declara #define MyVersion: el instalador no se podrá generar."
}
elseif ($issVersion -ne $version) {
    Write-Warning "tools/installer.iss dice MyVersion=$issVersion pero el .csproj dice $version. Sincronízalos antes de generar el setup (iscc acepta /DMyVersion=$version)."
}

# 1) Auto-tests
if (-not $SkipTests) {
    Write-Host ""
    Write-Host "== Auto-tests ==" -ForegroundColor Cyan
    dotnet run --project $testProject -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw "Los auto-tests han fallado." }
}

# 2) Publicacion
# El instalador (y el README/AGENTS.md) esperan dist\Selbar-<version> y
# Selbar-<version>-portable.zip para win-x64; otras arquitecturas llevan el
# RID en el nombre para no romper ese contrato.
$suffix = if ($Runtime -eq 'win-x64') { '' } else { "-$Runtime" }
$publishDir = Join-Path $root "dist\Selbar-$version${suffix}"
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

$publishArgs = @('publish', $project, '-c', $Configuration, '-o', $publishDir, '--nologo', '-v', 'minimal')
if ($SelfContained) {
    $publishArgs += @('-r', $Runtime, '--self-contained', 'true',
                      '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true')
}

Write-Host ""
Write-Host "== Compilando ==" -ForegroundColor Cyan
dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "La compilacion ha fallado." }

$exe = Join-Path $publishDir 'Selbar.exe'
if (-not (Test-Path $exe)) { throw "La publicación no generó $exe." }

# 3) Empaquetado portable (settings junto al .exe, no en %AppData%)
Set-Content -Path (Join-Path $publishDir 'portable.txt') `
    -Value "Selbar en modo portable: los ajustes se guardan en la carpeta .\data" -Encoding UTF8
Copy-Item (Join-Path $root 'README.md') (Join-Path $publishDir 'LEEME.md') -Force
Get-ChildItem $publishDir -Filter '*.pdb' -ErrorAction SilentlyContinue | Remove-Item -Force

if ($NoZip) {
    Write-Host "OK -> $publishDir" -ForegroundColor Green
    return
}

$zip = Join-Path $root "Selbar-$version${suffix}-portable.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zip -CompressionLevel Optimal

$sizeMb = [Math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host ""
Write-Host ("OK -> {0} ({1} MB)" -f $zip, $sizeMb) -ForegroundColor Green
