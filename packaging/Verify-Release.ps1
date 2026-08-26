<#
.SYNOPSIS
    Release-integrity gate: verifies version parity across sources and inspects the
    real packed artifacts (not just source files). Fails loudly on any mismatch.

.DESCRIPTION
    Checks, in order:
      1. Source parity: csproj <Version> vs packaging/Package.appxmanifest Identity Version.
      2. For each MSIX (default: packaging/out/PdfLiteViewer-<ver>-win-{x64,arm64}.msix),
         unpacks it with makeappx and verifies:
           - embedded AppxManifest.xml Identity Name / Publisher / Version / ProcessorArchitecture
           - PdfLiteViewer.exe present with FileVersion and ProductVersion == expected
           - every Assets\*.png the manifest references
           - a satellite PdfLiteViewer.resources.dll for every shipped language
           - the WebView2 loader and managed assemblies
           - the artifact filename version matches the embedded package version
           - provenance: the +<sha> in ProductVersion equals git HEAD, and the
             working tree is clean (-SkipProvenance bypasses, for deliberate
             pre-commit builds only)
      3. Zips (auto-discovered for the version when -Zips is omitted; both RIDs
         required): exe at the archive root, no MSIX residue, all satellites +
         WebView2Loader.dll, name matches, and the embedded exe's version and
         provenance check out too.
      4. -TagCheck: HEAD carries tag v<version>.
      5. -WingetCheck: packaging/winget manifests for <version> exist and reference
         v<version> URLs.

.EXAMPLE
    .\packaging\Verify-Release.ps1                        # MSIX + auto-discovered zips for this version
    .\packaging\Verify-Release.ps1 -TagCheck -WingetCheck # after tagging / adding winget manifests
#>
param(
    [string]$ExpectedVersion = "",
    [string[]]$MsixPaths = @(),
    [string[]]$Zips = @(),
    [switch]$TagCheck,
    [switch]$WingetCheck,
    # Skips the built-from-HEAD check for deliberate pre-commit builds only.
    [switch]$SkipProvenance
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$failures = New-Object System.Collections.Generic.List[string]
function Fail([string]$msg) { $failures.Add($msg); Write-Host "FAIL  $msg" -ForegroundColor Red }
function Ok([string]$msg)   { Write-Host "ok    $msg" -ForegroundColor Green }

$headSha = ""
try { $headSha = (git -C $root rev-parse HEAD).Trim() } catch { }

# The SDK stamps the build's commit into ProductVersion as "+<sha>". An artifact
# built from any other commit than the one being verified must never pass - the
# 1.0.15 review round caught exactly that: a gate blessing pre-fix binaries.
function Test-Provenance([string]$label, [string]$productVersion) {
    if ($SkipProvenance) { return }
    $sha = ($productVersion -split '\+')[1]
    if (-not $headSha) { Fail "${label}: cannot resolve HEAD - provenance unverifiable (pass -SkipProvenance to bypass deliberately)" }
    elseif (-not $sha) { Fail "${label}: executable carries no source revision" }
    elseif ($sha -ne $headSha) { Fail "${label}: built from $sha but HEAD is $headSha - rebuild from the current commit (or -SkipProvenance for a deliberate pre-commit build)" }
    else { Ok "${label}: built from HEAD ($sha)" }
}

# A dirty tree defeats provenance entirely: the sha stamps HEAD even when the build
# included uncommitted edits - exactly how 1.0.15 binaries once carried 1.0.14's sha.
if (-not $SkipProvenance) {
    $dirty = ""
    try { $dirty = (git -C $root status --porcelain) -join "`n" } catch { }
    if ($dirty) { Fail "working tree has uncommitted changes - artifacts cannot be tied to a commit (commit first, or pass -SkipProvenance)" }
}

# ---- authoritative version -------------------------------------------------
$csprojPath = Join-Path $root "src\PdfLiteViewer\PdfLiteViewer.csproj"
[xml]$csproj = Get-Content $csprojPath
$csprojVersion = ($csproj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if (-not $csprojVersion) { throw "No <Version> in $csprojPath" }
if (-not $ExpectedVersion) { $ExpectedVersion = $csprojVersion }

if ($csprojVersion -ne $ExpectedVersion) {
    Fail "csproj Version '$csprojVersion' != expected '$ExpectedVersion'"
} else { Ok "csproj Version $csprojVersion" }

# ---- manifest parity -------------------------------------------------------
$manifestPath = Join-Path $PSScriptRoot "Package.appxmanifest"
[xml]$manifest = Get-Content $manifestPath
$manifestVersion = $manifest.Package.Identity.Version
if ($manifestVersion -ne "$ExpectedVersion.0") {
    Fail "Package.appxmanifest Identity Version '$manifestVersion' != '$ExpectedVersion.0'"
} else { Ok "Package.appxmanifest Identity Version $manifestVersion" }

$expectedIdentityName = "GreenYogaInc.PDFLiteViewer"
$expectedPublisher = "CN=1F15826A-1F07-4E59-AC9A-622A84CC59FF"
if ($manifest.Package.Identity.Name -ne $expectedIdentityName) { Fail "manifest Identity Name '$($manifest.Package.Identity.Name)'" }
if ($manifest.Package.Identity.Publisher -ne $expectedPublisher) { Fail "manifest Publisher '$($manifest.Package.Identity.Publisher)'" }

$languages = @($manifest.Package.Resources.Resource | ForEach-Object { $_.Language }) | Where-Object { $_ }
# English ships inside the neutral assembly; every other language needs a satellite dir.
$satelliteLangs = @($languages | Where-Object { $_ -ne "en-US" })
# The manifest's language list must agree with what the csproj actually ships — a
# trimmed <Resources> list must not silently turn the satellite check into a no-op.
$csprojLangs = @((($csproj.Project.PropertyGroup |
    Where-Object { $_.SatelliteResourceLanguages } |
    Select-Object -First 1).SatelliteResourceLanguages -split ';') |
    Where-Object { $_ -and $_ -ne 'en' })
$langDiff = Compare-Object @($satelliteLangs | Sort-Object) @($csprojLangs | Sort-Object)
if ($langDiff) {
    Fail "manifest/csproj language lists differ: $(($langDiff | ForEach-Object { "$($_.SideIndicator)$($_.InputObject)" }) -join ' ')"
} else { Ok "manifest/csproj language lists agree ($($satelliteLangs.Count) satellites)" }

$expectedAssets = @("StoreLogo.png", "Square150x150Logo.png", "Square44x44Logo.png",
                    "Square44x44Logo.targetsize-24_altform-unplated.png",
                    "Wide310x150Logo.png", "Square310x310Logo.png")

# ---- MSIX inspection -------------------------------------------------------
if ($MsixPaths.Count -eq 0) {
    $MsixPaths = @("win-x64", "win-arm64") | ForEach-Object {
        Join-Path $PSScriptRoot "out\PdfLiteViewer-$ExpectedVersion-$_.msix" }
}

$makeappx = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\makeappx.exe" -ErrorAction SilentlyContinue |
    Sort-Object FullName -Descending | Select-Object -First 1
if (-not $makeappx) { throw "makeappx.exe not found (Windows SDK required)" }

foreach ($msix in $MsixPaths) {
    if (-not (Test-Path $msix)) { Fail "missing artifact: $msix"; continue }
    $name = Split-Path $msix -Leaf
    $rid = if ($name -match "win-arm64") { "arm64" } elseif ($name -match "win-x64") { "x64" } else { "?" }

    $unpack = Join-Path ([System.IO.Path]::GetTempPath()) "plv-verify-$([guid]::NewGuid().ToString('n'))"
    & $makeappx.FullName unpack /p $msix /d $unpack /o | Out-Null
    if ($LASTEXITCODE -ne 0) { Fail "${name}: makeappx unpack failed"; continue }
    try {
        [xml]$appx = Get-Content (Join-Path $unpack "AppxManifest.xml")
        $id = $appx.Package.Identity
        if ($id.Name -ne $expectedIdentityName)      { Fail "${name}: packed Identity Name '$($id.Name)'" }
        if ($id.Publisher -ne $expectedPublisher)    { Fail "${name}: packed Publisher '$($id.Publisher)'" }
        if ($id.Version -ne "$ExpectedVersion.0")    { Fail "${name}: packed Version '$($id.Version)' != '$ExpectedVersion.0'" } else { Ok "${name}: packed Version $($id.Version)" }
        if ($id.ProcessorArchitecture -ne $rid)      { Fail "${name}: ProcessorArchitecture '$($id.ProcessorArchitecture)' != '$rid'" } else { Ok "${name}: ProcessorArchitecture $rid" }

        $exe = Join-Path $unpack "PdfLiteViewer.exe"
        if (-not (Test-Path $exe)) { Fail "${name}: PdfLiteViewer.exe missing" }
        else {
            $vi = (Get-Item $exe).VersionInfo
            $fileVer3 = ($vi.FileVersion -split '\.')[0..2] -join '.'
            $prodVer3 = (($vi.ProductVersion -split '\+')[0] -split '\.')[0..2] -join '.'
            if ($fileVer3 -ne $ExpectedVersion) { Fail "${name}: exe FileVersion '$($vi.FileVersion)' != $ExpectedVersion" } else { Ok "${name}: exe FileVersion $($vi.FileVersion)" }
            if ($prodVer3 -ne $ExpectedVersion) { Fail "${name}: exe ProductVersion '$($vi.ProductVersion)' != $ExpectedVersion" } else { Ok "${name}: exe ProductVersion $($vi.ProductVersion)" }
            Test-Provenance $name $vi.ProductVersion
        }

        $missingAssets = @($expectedAssets | Where-Object { -not (Test-Path (Join-Path $unpack "Assets\$_")) })
        if ($missingAssets) { Fail "${name}: missing assets: $($missingAssets -join ', ')" } else { Ok "${name}: all $($expectedAssets.Count) tile assets present" }

        $missingSat = @($satelliteLangs | Where-Object {
            -not (Test-Path (Join-Path $unpack "$_\PdfLiteViewer.resources.dll")) })
        if ($missingSat) { Fail "${name}: missing satellites: $($missingSat -join ', ')" } else { Ok "${name}: $($satelliteLangs.Count) language satellites present" }

        if (-not (Test-Path (Join-Path $unpack "WebView2Loader.dll"))) { Fail "${name}: WebView2Loader.dll missing" } else { Ok "${name}: WebView2Loader.dll present" }
        if (-not (Test-Path (Join-Path $unpack "Microsoft.Web.WebView2.Wpf.dll"))) { Fail "${name}: Microsoft.Web.WebView2.Wpf.dll missing" } else { Ok "${name}: WebView2 managed assemblies present" }
    }
    finally {
        Remove-Item $unpack -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# ---- portable zip inspection ----------------------------------------------
# The winget manifests ship from these zips, so "no zips given" must mean
# "discover them", never "skip the section and still print PASSED".
if ($Zips.Count -eq 0) {
    $Zips = @(Get-ChildItem (Join-Path $PSScriptRoot "out\PdfLiteViewer-$ExpectedVersion-win-*.zip") -ErrorAction SilentlyContinue |
        ForEach-Object { $_.FullName })
}
# Both architectures ship; a lone zip must never read as full coverage.
foreach ($ridName in @("win-x64", "win-arm64")) {
    if (-not ($Zips | Where-Object { $_ -match [regex]::Escape("PdfLiteViewer-$ExpectedVersion-$ridName.zip") })) {
        Fail "no $ridName portable zip for $ExpectedVersion (run Build-Zip.ps1 -Rid $ridName)"
    }
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
foreach ($zip in $Zips) {
    if (-not (Test-Path $zip)) { Fail "missing zip: $zip"; continue }
    $zn = Split-Path $zip -Leaf
    if ($zn -notmatch [regex]::Escape("PdfLiteViewer-$ExpectedVersion-win-")) { Fail "${zn}: filename does not carry version $ExpectedVersion" }

    $archive = [System.IO.Compression.ZipFile]::OpenRead($zip)
    try {
        $entries = $archive.Entries.FullName
        if ($entries -notcontains "PdfLiteViewer.exe") { Fail "${zn}: PdfLiteViewer.exe not at archive root" } else { Ok "${zn}: exe at archive root" }
        $residue = @($entries | Where-Object { $_ -match '^\[Content_Types\]\.xml$|^AppxManifest\.xml$|^Assets/' })
        if ($residue) { Fail "${zn}: MSIX residue: $($residue -join ', ')" } else { Ok "${zn}: no MSIX residue" }
        $missingSat = @($satelliteLangs | Where-Object { $lang = $_
            -not ($entries | Where-Object { $_ -eq "$lang/PdfLiteViewer.resources.dll" -or $_ -eq "$lang\PdfLiteViewer.resources.dll" }) })
        if ($missingSat) { Fail "${zn}: missing satellites: $($missingSat -join ', ')" } else { Ok "${zn}: satellites present" }
        if ($entries -notcontains "WebView2Loader.dll") { Fail "${zn}: WebView2Loader.dll missing" } else { Ok "${zn}: WebView2Loader.dll present" }

        # The zip's own executable must match the version AND the commit under
        # verification - the 1.0.14 zips were built from two different source states.
        $exeEntry = $archive.Entries | Where-Object { $_.FullName -eq "PdfLiteViewer.exe" } | Select-Object -First 1
        if ($exeEntry) {
            $tmpExe = Join-Path ([System.IO.Path]::GetTempPath()) "plv-zip-$([guid]::NewGuid().ToString('n')).exe"
            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($exeEntry, $tmpExe, $true)
            try {
                $zvi = (Get-Item $tmpExe).VersionInfo
                $zFileVer3 = ($zvi.FileVersion -split '\.')[0..2] -join '.'
                if ($zFileVer3 -ne $ExpectedVersion) { Fail "${zn}: exe FileVersion '$($zvi.FileVersion)' != $ExpectedVersion" } else { Ok "${zn}: exe FileVersion $($zvi.FileVersion)" }
                Test-Provenance $zn $zvi.ProductVersion
            }
            finally { Remove-Item $tmpExe -Force -ErrorAction SilentlyContinue }
        }
    }
    finally { $archive.Dispose() }
    Ok "${zn}: SHA256 $((Get-FileHash $zip -Algorithm SHA256).Hash)"
}

# ---- optional tag check ----------------------------------------------------
if ($TagCheck) {
    $tags = git -C $root tag --points-at HEAD
    if ($tags -notcontains "v$ExpectedVersion") { Fail "HEAD does not carry tag v$ExpectedVersion (tags here: $tags)" }
    else { Ok "HEAD tagged v$ExpectedVersion" }
}

# ---- optional winget check -------------------------------------------------
if ($WingetCheck) {
    $wgDir = Join-Path $PSScriptRoot "winget\manifests\g\GreenYogaInc\PDFLiteViewer\$ExpectedVersion"
    $wgInstaller = Join-Path $wgDir "GreenYogaInc.PDFLiteViewer.installer.yaml"
    if (-not (Test-Path $wgInstaller)) { Fail "winget installer manifest missing: $wgInstaller" }
    else {
        $installer = Get-Content $wgInstaller -Raw
        $okUrls = $true
        foreach ($ridName in @("x64", "arm64")) {
            if ($installer -notmatch [regex]::Escape("v$ExpectedVersion/PdfLiteViewer-$ExpectedVersion-win-$ridName.zip")) {
                Fail "winget installer.yaml: $ridName URL is not v$ExpectedVersion"; $okUrls = $false
            }
        }
        if ($okUrls) { Ok "winget manifests reference v$ExpectedVersion" }
    }
}

Write-Host ""
if ($failures.Count -gt 0) {
    Write-Host "VERIFY FAILED - $($failures.Count) problem(s):" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    exit 1
}
Write-Host "VERIFY PASSED - version $ExpectedVersion is consistent across sources and artifacts." -ForegroundColor Green
exit 0
