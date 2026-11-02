# Test coverage

`PdfLiteViewer.dll` has 100% line coverage and 100% branch coverage, as measured by
coverlet across the test harnesses. Some of that coverage comes from injecting states
production cannot reach; [the list below](#covered-only-by-injecting-a-state) names them.

## Measuring

```powershell
tools\Measure-Coverage.ps1
tools\Measure-Coverage.ps1 -MinLine 100 -MinBranch 100   # fail below these figures
```

The script builds Debug and merges five runs: `ChapterSmoke` over the fixtures, then
`HangProbe` with `--about-check`, `--startup-check`, `--real-print-to-pdf` and the full
probe. Results go to `coverage\`, which is not tracked.

A full measurement needs:

- An interactive desktop of at least 1280x900. HangProbe shows real windows and takes the
  foreground, so don't type during a run.
- The `Microsoft Print to PDF` queue. Without it, the real-print check skips and
  `PrintJob.SubmitToSpooler` stays uncovered.
- The script's default of 600 pages. At 40 pages the sidebar never recycles a chapter
  container, so the echo-guard check fails. Sizes in between were not measured.

## Results

| | Line | Branch |
|---|---|---|
| Before this work | 85.86% (1349/1571) | 74.88% (632/844) |
| Now | 100% (1578/1578) | 100% (808/808) |

Every file is fully covered. These are the counts per file:

| File | Lines | Branches |
|---|---|---|
| `AboutWindow.xaml.cs` | 153 | 54 |
| `App.xaml.cs` | 57 | 14 |
| `AppSupportClient.cs` | 70 | 80 |
| `ChapterItem.cs` | 35 | 14 |
| `Loc.cs` | 21 | 10 |
| `MainWindow.xaml.cs` | 740 | 426 |
| `PageItem.cs` | 9 | 2 |
| `PdfDoc.cs` | 144 | 48 |
| `PdfPrintPaginator.cs` | 37 | 0 |
| `PrintJob.cs` | 128 | 44 |
| `PrintPreviewWindow.xaml.cs` | 182 | 112 |
| `SupportNavigationPolicy.cs` | 2 | 4 |

## What the number means

The figure says every line and branch coverlet can see ran at least once. It does not say
the code is correct.

- **Not counted:** XAML-generated code under `obj/`, branches coverlet filters out as
  compiler-generated, `tools/`, the packaging scripts and `tools/make_icons.py`.
- **Assertions:** the checks assert behaviour rather than just running lines. As a spot
  check, 17 product lines were broken by hand, one at a time, and the check meant for each
  one failed. That is a sample, not full mutation testing.
- **No exclusions:** nothing is marked `[ExcludeFromCodeCoverage]`. Code proven dead was
  deleted instead.

## Covered only by injecting a state

Production cannot get into these states on a healthy machine, so a check puts it there.
These checks verify what each fallback does, not that it can ever run.

| Code | Why production doesn't reach it | How the check reaches it |
|---|---|---|
| `MainWindow.PagesPanel` returning null, and the fallbacks in `TopVisiblePage` and `VisibleRange` | `ApplyTemplate` on the presenter always builds the panel | Reflection swaps in an untemplated presenter |
| `child is not FrameworkElement` in the same two methods | Item containers are always framework elements | Reflection injects a panel holding a bare `UIElement` |
| `PanelOffsetScale` returning 1.0 | Called only once layout has run | Reflection calls it on a window with no layout |
| `DisplayWidth > 0` false in `UpdateRenderedPagesAsync` | Page sizes are validated when the document opens | A slot's width is set to 0 by hand |
| Page-count mismatch in `PdfDoc.RequireConsistentSizes` | PDFium returns one size per page | Called directly with a mismatched list |
| Colour conversion in `PdfDoc.ToBitmapSource` | PDFium returns BGRA | Called directly with an RGBA bitmap |
| Last catch in `PrintJob.ResolveQueue` | A real server failing every call needs a dead spooler | Called with a null server |
| `PrintJob.EnumerateQueues` and `PaperFor` with no print system | The spooler is running | `NewServer` and `DefaultQueue` replaced with failing delegates |
| `PrintJob.PickQueue` finding a match | Only a print-server connection gets there | Called with fake queues |
| Fallbacks in `PrintJob.FirstTicket` | Every local driver reports both tickets | Called with delegates returning null |
| `App.OnUnhandledException` | A real one ends the process and loses the coverage data | Called directly |
| Fallbacks in `AboutWindow.AppVersion` and `ReadLicense` | The shipped assembly has a version and the licence | Called with a fake `Assembly` |
| The 20-second timeout in `AppSupportClient.SendAsync` | A check should not wait 20 seconds | The fake handler throws `TaskCanceledException` |

## Not verified

- **Mixed-DPI monitors.** `app.manifest` makes the viewer per-monitor aware and a check
  confirms the real exe reports that, but `OnDpiChanged` is driven by
  `VisualTreeHelper.SetRootDpi` or a direct call. It has not been seen on real monitors with
  different scales.
- **Physical printers.** The only real job goes to `Microsoft Print to PDF`. Everywhere else
  `PrintJob.Submit` is a throwing guard, because a preview window selects the default printer
  on its own.
- **Print-server queues.** `PickQueue` is tested as logic only.
- **The live support endpoint.** Every check uses a fake `HttpMessageHandler`.

## Worth knowing

- **Timing:** the "visible but not yet loaded" arm of `PrintPreviewWindow.LoadPrintersAsync`
  needs the printer list to arrive during `ShowDialog`, before `Loaded` fires. If a run shows
  807/808 branches, look there first.
- **Dead library path:** PdfPig 0.1.15 never produces an `EmbeddedBookmarkNode`. It drops a
  GoToE outline item before `PdfDoc` sees it, so the exclusion in `MapBookmarks` only guards
  against a future PdfPig.
- **Leftover files:** HangProbe still writes `hangprobe-*.png` to `%TEMP%` for eyeballing, as
  the README describes, and does not delete them.
