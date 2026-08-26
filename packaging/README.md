# Packaging & Distribution

PDF Lite Viewer is freeware (MIT, © 2026 Green Yoga Inc). Two standard Windows
distribution channels are prepared here.

The release version lives in **one** place: `<Version>` in
`src/PdfLiteViewer/PdfLiteViewer.csproj`. `Package.appxmanifest` must carry the
same value (`<Version>.0`); `Build-Msix.ps1` refuses to pack when they disagree,
and `Verify-Release.ps1` checks the *packed artifacts* — not just the sources.
Nothing below ever needs a version number typed by hand.

## 1. Microsoft Store (MSIX)

Build both architectures:

```powershell
.\packaging\Build-Msix.ps1                  # x64
.\packaging\Build-Msix.ps1 -Rid win-arm64   # ARM64
```

Output: `packaging/out/PdfLiteViewer-<version>-<rid>.msix` (version derived from
the manifest identity, which the parity gate ties to the csproj).

Store identity (already assigned by Partner Center, product `9NP0154N0JR5`) is
committed in `Package.appxmanifest` and must not change:
`GreenYogaInc.PDFLiteViewer` / `CN=1F15826A-1F07-4E59-AC9A-622A84CC59FF`.

Submission steps:

1. Verify the artifacts first (see *Release verification* below).
2. In Partner Center, create a new submission for **PDF Lite Viewer**, upload
   both the x64 and arm64 `.msix`, price **Free**.
3. Screenshots come from `packaging/store-screenshots/` (captions in
   `captions.md` there); regenerate them with `tools/StoreShots` — never reuse
   captures from an older release.
4. The Store signs the package during ingestion — no local certificate needed.

## 2. Portable zip (GitHub release / sideload)

```powershell
.\packaging\Build-Zip.ps1                  # x64
.\packaging\Build-Zip.ps1 -Rid win-arm64   # ARM64
```

Output: `packaging/out/PdfLiteViewer-<version>-<rid>.zip`, built from a wiped
stage with `PdfLiteViewer.exe` at the archive root (required by the winget
manifest's `NestedInstallerFiles`) — do not zip the publish folder by hand.
`PdfLiteViewer.exe` runs standalone; no installer.

## 3. winget (Windows Package Manager)

After the GitHub release exists (so the URLs are immutable):

1. Create `winget/manifests/g/GreenYogaInc/PDFLiteViewer/<version>/` by copying
   the previous version's three yaml files; bump `PackageVersion`,
   `InstallerUrl` (both architectures) and `ReleaseNotesUrl` to the new tag, and
   set each `InstallerSha256` from the *uploaded* zips
   (`Get-FileHash packaging\out\PdfLiteViewer-<version>-win-x64.zip`).
   Keep `ArchiveBinariesDependOnPath: true` and the `NestedInstallerFiles`
   block unchanged.
2. Validate: `winget validate --manifest packaging\winget\manifests\g\GreenYogaInc\PDFLiteViewer\<version>\`
3. Submit a PR to https://github.com/microsoft/winget-pkgs under
   `manifests/g/GreenYogaInc/PDFLiteViewer/<version>/` (only when separately
   authorized).

## Release verification

Run after building, and again after tagging:

```powershell
.\packaging\Verify-Release.ps1 -Zips (Get-ChildItem packaging\out\PdfLiteViewer-*-win-*.zip).FullName
.\packaging\Verify-Release.ps1 -TagCheck -WingetCheck     # once the tag + winget manifests exist
```

It unpacks each MSIX and checks the embedded identity/version/architecture, the
executable's FileVersion/ProductVersion, tile assets, all language satellites,
and the WebView2 loader; for zips it checks root layout, MSIX residue, and
satellites. Non-zero exit means do not ship.

Also run the app-level gates before any submission:

```powershell
dotnet build PdfLiteViewer.slnx -c Release          # zero warnings expected
dotnet run --project tools\ChapterSmoke -- tools\fixtures\*.pdf   # glob expanded by the tool
dotnet run --project tools\HangProbe -- 3000        # UI responsiveness + regression checks
```

## Store screenshots

`tools/StoreShots` rebuilds the entire tracked screenshot set from the current
Release build — consistent resolution, deterministic demo document, captions:

```powershell
dotnet run --project tools\StoreShots -c Release
```

See `packaging/store-screenshots/captions.md` for the per-image captions and
`tools/StoreShots/Program.cs` for the scene list. Every image must be at least
1366x768 (Microsoft desktop minimum); the tool enforces the set's resolution
and fails on any undersized capture.
