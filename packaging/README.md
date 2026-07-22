# Packaging & Distribution

PDF Lite Viewer is freeware (MIT, © 2026 Green Yoga Inc). Two standard Windows
distribution channels are prepared here.

## 1. Microsoft Store (MSIX)

Build the package:

```powershell
.\packaging\Build-Msix.ps1                  # x64
.\packaging\Build-Msix.ps1 -Rid win-arm64   # ARM64
```

Output: `packaging/out/PdfLiteViewer-<version>-<rid>.msix` (version derived from `Package.appxmanifest`).

Submission steps (one-time Partner Center setup):

1. Register a Microsoft Partner Center developer account (one-time fee, ~$19 individual / $99 company): https://partner.microsoft.com/dashboard/registration
2. Reserve the app name **PDF Lite Viewer**. Partner Center then shows the real
   `Identity Name` / `Publisher` values (Product identity page).
3. Put those values into `Package.appxmanifest` (`<Identity>` element), rebuild the MSIX.
4. Create a new submission, upload the `.msix` (upload both x64 and arm64 for broad device coverage), set price **Free**, fill listing (description/screenshots — screenshots from a test run are in the repo notes), and submit for certification.
5. The Store signs the package during ingestion — no local code-signing certificate needed.

## 2. winget (Windows Package Manager)

`winget/` contains a manifest template. After publishing a GitHub (or other public)
release with the MSIX or a zip of the self-contained publish output:

1. Fill in the real `InstallerUrl` + `InstallerSha256`
   (`Get-FileHash .\PdfLiteViewer-1.0.0-win-x64.msix`).
2. Validate: `winget validate --manifest winget\`
3. Submit a PR to https://github.com/microsoft/winget-pkgs under
   `manifests/g/GreenYogaInc/PDFLiteViewer/1.0.0/`.

## Sideload / direct download

For a plain freeware download (no store), ship the publish folder as a zip:

```powershell
dotnet publish src\PdfLiteViewer -c Release -r win-x64 --self-contained true -o dist\PdfLiteViewer
Compress-Archive dist\PdfLiteViewer dist\PdfLiteViewer-1.0.0-win-x64.zip
```

No installer needed — `PdfLiteViewer.exe` runs standalone.
