<#
.SYNOPSIS
    Builds FlowType release packages.

.DESCRIPTION
    Produces two artifacts in dist\:

      FlowType-<version>-win-x64\        a plain folder build (recommended)
      FlowType-<version>-win-x64.zip     the same folder, zipped for sharing

    Both are self-contained: the .NET runtime is included, so users do not need
    to install anything. Publishing is deliberately done WITHOUT single-file
    packing, compression, or trimming — those rewrite the payload into a
    self-extracting blob, which is the same shape malware packers use and the
    single biggest cause of false-positive antivirus detections for indie apps.

.PARAMETER Sign
    Also Authenticode-sign the binaries. Requires a code-signing certificate in
    the current user's store; pass its thumbprint via -Thumbprint. Signing is
    what actually earns SmartScreen/AV trust — see SECURITY.md.

.EXAMPLE
    pwsh build\publish.ps1
    pwsh build\publish.ps1 -Sign -Thumbprint ABC123...
#>
[CmdletBinding()]
param(
    [switch]$Sign,
    [string]$Thumbprint,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project  = Join-Path $repoRoot 'src\FlowType\FlowType.csproj'
$distRoot = Join-Path $repoRoot 'dist'

# Version comes from the project file so there is exactly one source of truth.
[xml]$csproj = Get-Content $project
$version = ($csproj.Project.PropertyGroup.Version | Where-Object { $_ }) | Select-Object -First 1
$name    = "FlowType-$version-win-x64"
$outDir  = Join-Path $distRoot $name

Write-Host "Publishing FlowType $version ..." -ForegroundColor Cyan

if (Test-Path $outDir) { Remove-Item -Recurse -Force $outDir }
New-Item -ItemType Directory -Force $distRoot | Out-Null

& dotnet publish $project -c $Configuration -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -p:PublishTrimmed=false -p:DebugType=embedded `
    -o $outDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

# Ship the docs users actually need next to the executable.
foreach ($doc in @('README.md', 'LICENSE', 'SECURITY.md')) {
    $path = Join-Path $repoRoot $doc
    if (Test-Path $path) { Copy-Item $path $outDir }
}

if ($Sign) {
    if (-not $Thumbprint) { throw 'Pass -Thumbprint when using -Sign.' }
    $signtool = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Recurse `
        -Filter signtool.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match 'x64' } | Select-Object -Last 1
    if (-not $signtool) { throw 'signtool.exe not found (install the Windows SDK).' }

    # Sign the app's own binaries. Timestamping keeps signatures valid after
    # the certificate expires.
    $targets = Get-ChildItem $outDir -Include 'FlowType.exe', 'FlowType.dll' -Recurse
    foreach ($target in $targets) {
        & $signtool.FullName sign /sha1 $Thumbprint /fd SHA256 `
            /tr http://timestamp.digicert.com /td SHA256 $target.FullName
        if ($LASTEXITCODE -ne 0) { throw "signing failed for $($target.Name)" }
    }
    Write-Host 'Signed.' -ForegroundColor Green
}

$zipPath = Join-Path $distRoot "$name.zip"
if (Test-Path $zipPath) { Remove-Item -Force $zipPath }
Compress-Archive -Path (Join-Path $outDir '*') -DestinationPath $zipPath

# NOTE: no single-file (-p:PublishSingleFile=true) artifact is produced.
# Whisper.net resolves its native libraries from runtimes\win-x64\native next
# to the executable; single-file bundling flattens that layout, so the bundled
# exe starts and then fails at model load with "Native Library not found in
# default paths". Verified — do not re-add without testing --selftest against
# the bundled exe. The folder + zip below is also the antivirus-friendlier
# shape (see SECURITY.md).

$sizeMB = [math]::Round((Get-ChildItem $outDir -Recurse |
    Measure-Object -Property Length -Sum).Sum / 1MB)
Write-Host ""
Write-Host "Done." -ForegroundColor Green
Write-Host "  Folder : $outDir  ($sizeMB MB)"
Write-Host "  Zip    : $zipPath"
Write-Host "  Run    : $outDir\FlowType.exe"
