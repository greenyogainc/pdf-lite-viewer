# PDF Lite Viewer

A **free, lightweight PDF viewer for Windows**. No bloat — it opens PDFs and
displays them well, and that's it.

© 2026 Green Yoga Inc · Freeware, released under the [MIT License](LICENSE).

## Features

- **Three viewing modes**, toggleable from the toolbar or keyboard:
  - **Facing** — two pages side by side, book layout (press `2`)
  - **Single** — one page at a time; press `F11` for distraction-free full screen (press `1`)
  - **Scroll** — continuous vertical scrolling through the whole document (press `3`)
- Fast PDFium rendering with lazy page loading — large documents open instantly
- Zoom (`Ctrl` `+`/`−`/`0`, `Ctrl`+wheel), fit-to-view, page navigation, go-to-page
- Open via dialog (`Ctrl+O`), drag & drop, or double-click a `.pdf` (file association)
- That's the whole feature list, by design.

## Keyboard reference

| Key | Action |
|---|---|
| `Ctrl+O` | Open PDF |
| `1` / `2` / `3` | Single / Facing / Continuous mode |
| `F11` (`Esc` to exit) | Full screen |
| `←` `→` / `PgUp` `PgDn` | Previous / next page |
| `Home` / `End` | First / last page |
| `Ctrl` `+` / `−` / `0` | Zoom in / out / fit |

## Building

Requires the .NET 10 SDK on Windows.

```powershell
dotnet build -c Release
dotnet run --project src\PdfLiteViewer
```

## Distribution

See [packaging/README.md](packaging/README.md) for Microsoft Store (MSIX) and
winget submission, plus plain zip sideloading.

## Tech

WPF (.NET 10) + [PDFtoImage](https://github.com/sungaila/PDFtoImage) (PDFium +
SkiaSharp). Pages render lazily on a background thread at the current zoom and
are dropped when scrolled far off-screen, keeping memory flat on huge documents.
