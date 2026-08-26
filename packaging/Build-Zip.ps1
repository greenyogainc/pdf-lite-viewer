<#
.SYNOPSIS
    Builds the portable zip for one RID, from a clean stage, with the exe at the
    archive root (which is what the winget NestedInstallerFiles entry requires).

.DESCRIPTION
    The 1.0.14 zips were built from a stage that was never wiped and picked up MSIX
    residue ([Content_Types].xml); the previously documented ad-hoc Compress-Archive
    command would additionally have nested everything under a PdfLiteViewer\ folder,
    breaking `winget install`. This script owns the whole procedure instead:
      1. wipe packaging\out\zip-stage\<rid>
      2. dotnet publish (self-contained) into it, strip *.pdb
      3. zip its *contents* (flat) to packaging\out\PdfLiteViewer-<version>-<rid>.zip
      4. verify the archive: PdfLiteViewer.exe at the root, no [Content_Types].xml

.EXAMPLE
    .\packaging\Build-Zip.ps1                  # x64
    .\packaging\Build-Zip.ps1 -Rid win-arm64   # ARM64
#>
param(
    [string]$Rid = "win-x64",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$proj = Join-Path $root "src\PdfLiteViewer\PdfLiteViewer.csproj"
$outDir = Join-Path $PSScriptRoot "out"
$stage = Join-Path $outDir "zip-stage\$Rid"

[xml]$projXml = Get-Content $proj
$ver = ($projXml.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if (-not $ver) { throw "No <Version> in $proj" }

Write-Host "== Publishing $ver ($Configuration / $Rid) into a clean stage ==" -ForegroundColor Cyan
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
dotnet publish $proj -c $Configuration -r $Rid --self-contained true `
    -p:PublishSingleFile=false -o $stage
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Get-ChildItem $stage -Filter *.pdb -Recurse | Remove-Item -Force

$zip = Join-Path $outDir "PdfLiteViewer-$ver-$Rid.zip"
Write-Host "== Zipping $zip ==" -ForegroundColor Cyan
if (Test-Path $zip) { Remove-Item $zip -Force }
# $stage\* (not $stage) keeps the archive flat: entries at the root, no wrapper folder.
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip

Write-Host "== Verifying archive layout ==" -ForegroundColor Cyan
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($zip)
try {
    $entries = $archive.Entries.FullName
    if ($entries -notcontains "PdfLiteViewer.exe") {
        throw "PdfLiteViewer.exe is not at the archive root - winget NestedInstallerFiles would fail."
    }
    $residue = $entries | Where-Object { $_ -match '^\[Content_Types\]\.xml$|^AppxManifest\.xml$|^Assets/' }
    if ($residue) {
        throw "MSIX residue in the zip: $($residue -join ', ') - stage was not clean."
    }
}
finally {
    $archive.Dispose()
}

$hash = (Get-FileHash $zip -Algorithm SHA256).Hash
Write-Host "Done: $zip" -ForegroundColor Green
Write-Host "SHA256: $hash"
