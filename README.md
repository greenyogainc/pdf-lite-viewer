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
- **Chapter navigation** — a resizable sidebar (`F4`) shows the PDF's embedded
  chapters/bookmarks as a tree; clicking a chapter jumps to its page and the
  current chapter stays highlighted as you navigate. PDFs without an outline
  show a simple empty state.
- Zoom (`Ctrl` `+`/`−`/`0`, `Ctrl`+wheel), fit-to-view, **rotate** (`Ctrl+R` or toolbar), page navigation, go-to-page
- Open via dialog (`Ctrl+O`), drag & drop, or double-click a `.pdf` (file association)
- Print (`Ctrl+P`) through a built-in preview: printer picker, page ranges, copies,
  black & white and draft modes — the job is sent directly, no second OS dialog
- **About & support** (`F1`) — version and license info, plus a built-in web support
  form (see *Privacy & network use* below)
- That's the whole feature list, by design.

## Privacy & network use

PDF viewing is entirely local: documents never leave your machine and the app
sends no telemetry. The one thing that goes online is **Contact support** in the
About window — after an explicit click it loads the Green Yoga Inc support form
(`greenyogainc.com`) in an embedded browser view, and that page may use the
website's own analytics; the [Green Yoga Inc privacy policy](https://greenyogainc.com/privacy/)
applies to it. Nothing is loaded until you ask.

## Keyboard reference

| Key | Action |
|---|---|
| `Ctrl+O` | Open PDF |
| `Ctrl+P` | Print |
| `F1` | About & support |
| `F4` | Show / hide chapter sidebar |
| `1` / `2` / `3` | Single / Facing / Continuous mode |
| `F11` (`Esc` to exit) | Full screen |
| `←` `→` / `PgUp` `PgDn` | Previous / next page |
| `Home` / `End` | First / last page |
| `Ctrl` `+` / `−` / `0` | Zoom in / out / fit |
| `Ctrl+R` | Rotate pages 90° clockwise (view + print; file unchanged) |

## Building

Requires the .NET 10 SDK on Windows.

```powershell
dotnet build -c Release
dotnet run --project src\PdfLiteViewer
```

## Testing

```powershell
dotnet run --project tools\HangProbe -- 3000        # UI responsiveness + layout + regressions
dotnet run --project tools\ChapterSmoke -- tools\fixtures\*.pdf   # the tool expands the glob itself
```

`HangProbe` is the regression guard against frozen-window bugs. It generates a
many-page stress PDF, drives the real window through the operations that can block
the message pump (opening, mode switches, zoom, rotate, page jumps, the chapter
sidebar, the print preview, a 300 DPI print run) and, from a watchdog thread, times
how long the UI thread takes to service queued input. Any scenario over budget fails
the run. It then asserts the things a fast-but-wrong viewer would get wrong — pages
centred, scrollbar spanning the document, go-to-page landing on the right page, pages
actually rendering — and writes a PNG of each view mode to `%TEMP%\hangprobe-*.png`.

It needs an interactive desktop session, since it really shows windows.

## Distribution

See [packaging/README.md](packaging/README.md) for Microsoft Store (MSIX) and
winget submission, plus plain zip sideloading.

## Tech

WPF (.NET 10) + [PDFtoImage](https://github.com/sungaila/PDFtoImage) (PDFium +
SkiaSharp). Pages render lazily on a background thread at the current zoom and
are dropped when scrolled far off-screen, keeping memory flat on huge documents.
[PdfPig](https://github.com/UglyToad/PdfPig) is used read-only, on demand, to
extract the embedded outline for the chapter sidebar.
