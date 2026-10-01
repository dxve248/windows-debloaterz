<#
.SYNOPSIS
    Builds Windows Debloater into dist\Debloater.exe (self-contained, single file).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File build.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File build.ps1 -Configuration Debug
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [switch] $SkipIcon
)

$ErrorActionPreference = 'Stop'

$root  = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj  = Join-Path $root 'src\WindowsDebloater\WindowsDebloater.csproj'
$dist  = Join-Path $root 'dist'
$icon  = Join-Path $root 'tools\Make-Icon.ps1'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'The .NET SDK was not found on PATH. Install the .NET 10 SDK and try again.'
}

if (-not $SkipIcon) {
    Write-Host 'Generating icon...'
    & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $icon
    if ($LASTEXITCODE -ne 0) { throw 'Icon generation failed.' }
}

New-Item -ItemType Directory -Force -Path $dist | Out-Null

Write-Host "Publishing $Configuration -> $dist"
dotnet publish $proj `
    --configuration $Configuration `
    --runtime win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    --output $dist

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$exe = Join-Path $dist 'Debloater.exe'
if (-not (Test-Path $exe)) { throw "Expected $exe but it was not produced." }

$sizeMb = [Math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host ''
Write-Host "Built $exe  ($sizeMb MB)"
Write-Host 'Double-click it to run. It will ask for administrator rights on launch.'