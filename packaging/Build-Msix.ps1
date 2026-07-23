<#
.SYNOPSIS
    Builds PdfLiteViewer and packs it into an MSIX ready for Microsoft Store submission.

.DESCRIPTION
    1. dotnet publish (self-contained, single RID)
    2. Stages the publish output + Package.appxmanifest + Assets into an MSIX layout
    3. Packs with makeappx.exe (Windows SDK)
    Store submissions do NOT need to be signed locally — the Store signs the package.
    For sideloading/testing, sign with signtool and a trusted cert (see -SignThumbprint).

.EXAMPLE
    .\Build-Msix.ps1                  # x64 MSIX in packaging\out
    .\Build-Msix.ps1 -Rid win-arm64   # ARM64 MSIX
#>
param(
    [string]$Rid = "win-x64",
    [string]$Configuration = "Release",
    [string]$SignThumbprint = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$proj = Join-Path $root "src\PdfLiteViewer\PdfLiteViewer.csproj"
$outDir = Join-Path $PSScriptRoot "out"
$stage = Join-Path $outDir "layout-$Rid"

Write-Host "== Publishing ($Configuration / $Rid) ==" -ForegroundColor Cyan
# Clean the stage first — publish reuses this dir, and makeappx packs it wholesale, so
# a language/DLL removed in a later build would otherwise linger and ship stale.
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
dotnet publish $proj -c $Configuration -r $Rid --self-contained true `
    -p:PublishSingleFile=false -o $stage
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Write-Host "== Staging manifest and assets ==" -ForegroundColor Cyan
$manifestPath = Join-Path $stage "AppxManifest.xml"
Copy-Item (Join-Path $PSScriptRoot "Package.appxmanifest") $manifestPath -Force
Copy-Item (Join-Path $PSScriptRoot "Assets") $stage -Recurse -Force

# Debug symbols are dead weight in a Store submission.
Get-ChildItem $stage -Filter *.pdb -Recurse | Remove-Item -Force

# Stamp the CPU architecture into the package identity — without this, every
# architecture produces the same full name (..._Neutral_) and the Store
# rejects multi-arch submissions as duplicates.
$arch = switch ($Rid) {
    "win-arm64" { "arm64" }
    default     { "x64" }
}
[xml]$xml = Get-Content $manifestPath
$xml.Package.Identity.SetAttribute("ProcessorArchitecture", $arch)
$xml.Save($manifestPath)

Write-Host "== Locating makeappx.exe ==" -ForegroundColor Cyan
$makeappx = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\makeappx.exe" -ErrorAction SilentlyContinue |
    Sort-Object FullName -Descending | Select-Object -First 1
if (-not $makeappx) {
    throw "makeappx.exe not found. Install the Windows 10/11 SDK (winget install Microsoft.WindowsSDK.10.0.26100)."
}

# Derive the marketing version (first three parts) from the manifest identity
# so the filename never drifts from the packaged version.
$ver = ($xml.Package.Identity.Version -split '\.')[0..2] -join '.'
$msix = Join-Path $outDir "PdfLiteViewer-$ver-$Rid.msix"
Write-Host "== Packing $msix ==" -ForegroundColor Cyan
& $makeappx.FullName pack /d $stage /p $msix /o
if ($LASTEXITCODE -ne 0) { throw "makeappx failed" }

if ($SignThumbprint) {
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" |
        Sort-Object FullName -Descending | Select-Object -First 1
    Write-Host "== Signing ==" -ForegroundColor Cyan
    & $signtool.FullName sign /fd SHA256 /sha1 $SignThumbprint $msix
    if ($LASTEXITCODE -ne 0) { throw "signtool failed" }
}

Write-Host "Done: $msix" -ForegroundColor Green
