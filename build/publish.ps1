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
    the current user's store; pass its thumbprint via -Thumbprint.
    See build\RELEASING.md.

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
foreach ($doc in @('README.md', 'LICENSE', 'SECURITY.md', 'THIRD-PARTY-NOTICES.md')) {
    $path = Join-Path $repoRoot $doc
    if (Test-Path $path) { Copy-Item $path $outDir }
}

# ---------------------------------------------------------------------------
# Visual C++ runtime.
#
# whisper.dll and the ggml libraries import MSVCP140.dll, VCRUNTIME140.dll,
# VCRUNTIME140_1.dll, and (the CPU backend) VCOMP140.DLL. .NET's own
# vcruntime140_cor3.dll does NOT satisfy an import named VCRUNTIME140.dll, and
# FlowType ships as a plain zip with no installer — so on a machine without the
# VC++ 2015-2022 x64 redistributable the speech engine simply fails to load.
# Redistributing these app-local is licensed; they are listed in
# THIRD-PARTY-NOTICES.md.
$crtNames = @('msvcp140.dll', 'vcruntime140.dll', 'vcruntime140_1.dll', 'vcomp140.dll')

$crtSource = $null
$vsRedist = Get-ChildItem 'C:\Program Files\Microsoft Visual Studio' -Recurse -Directory `
    -Filter 'x64' -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match '\\VC\\Redist\\MSVC\\' } |
    ForEach-Object { Get-ChildItem $_.FullName -Directory -ErrorAction SilentlyContinue } |
    Where-Object { $_.Name -match 'CRT$' } | Select-Object -Last 1
if ($vsRedist) { $crtSource = $vsRedist.FullName }

foreach ($crt in $crtNames) {
    $from = $null
    if ($crtSource) {
        # OpenMP lives in its own redist folder beside the CRT one.
        $candidate = Join-Path $crtSource $crt
        if (Test-Path $candidate) { $from = $candidate }
        else {
            $openmp = Join-Path (Split-Path $crtSource) 'Microsoft.VC143.OpenMP'
            $candidate = Join-Path $openmp $crt
            if (Test-Path $candidate) { $from = $candidate }
        }
    }
    if (-not $from) {
        # Fall back to the installed redistributable on this machine. Same file,
        # same architecture; it is only a less canonical source.
        $candidate = Join-Path $env:SystemRoot "System32\$crt"
        if (Test-Path $candidate) { $from = $candidate }
    }
    if (-not $from) {
        throw "Visual C++ runtime '$crt' not found. Install the VC++ 2015-2022 x64 redistributable, or the Visual Studio C++ workload, and publish again. Shipping without it makes the speech engine fail to load on clean machines."
    }
    Copy-Item $from (Join-Path $outDir $crt) -Force
}

# A partial native set is worse than an obvious failure: the app would start and
# then fail at model load with a message nobody can act on.
$nativeDir = Join-Path $outDir 'runtimes\vulkan\win-x64'
$requiredNatives = @('whisper.dll', 'ggml-whisper.dll', 'ggml-base-whisper.dll',
                     'ggml-cpu-whisper.dll', 'ggml-vulkan-whisper.dll')
foreach ($native in $requiredNatives) {
    if (-not (Test-Path (Join-Path $nativeDir $native))) {
        throw "Missing native library '$native' in $nativeDir."
    }
}
foreach ($crt in $crtNames) {
    if (-not (Test-Path (Join-Path $outDir $crt))) { throw "Missing '$crt' in the publish output." }
}

# CPU-safety invariant: only the Vulkan backend may reference vulkan-1.dll, so a
# machine with no Vulkan loader still starts and falls back to CPU.
$exeBytes = [System.IO.File]::ReadAllBytes((Join-Path $outDir 'FlowType.exe'))
$exeText  = [System.Text.Encoding]::ASCII.GetString($exeBytes)
if ($exeText -match 'vulkan-1') { throw 'FlowType.exe references vulkan-1 directly; the CPU fallback would break.' }

# Runtimes for platforms this build cannot target are dead weight in the zip.
foreach ($dead in @('linux-x64', 'win-arm64', 'win-x86', 'osx')) {
    $path = Join-Path $outDir "runtimes\$dead"
    if (Test-Path $path) { Remove-Item -Recurse -Force $path }
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
