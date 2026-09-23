# Findings ledger — full codebase review (final disposition)
Repo: greenyogainc/pdf-lite-viewer · Branch base: d416e79 · Review branch: code-review/full-codebase-review-20260923-1137 · Final tip: 153defb

## Fixes landed (3 commits, 17 files, +297/-74)

| Commit | Files | Findings addressed |
|---|---|---|
| 97647b8 | App.xaml.cs, PdfDoc.cs | F-A-1, F-A-2 |
| 08ca019 | HangProbe/{Program,Shots,StressPdf,LayoutChecks}.cs, StoreShots/Program.cs, MainWindow.xaml.cs, PdfPrintPaginator.cs, PrintJob.cs, PrintPreviewWindow.xaml.cs | F-B-3, F-B-4, F-B-6, F-B-7, F-B-8, F-B-9, F-B-12 + partition-A follow-up edits |
| 153defb | AboutWindow.xaml, Strings.{ja,ko,zh-Hans,zh-Hant}.resx, packaging/README.md | F-D-1, F-D-3, F-C-4 |

## Confirmed and fixed (13)
| ID | Sev | Location | Status |
|---|---|---|---|
| F-A-1 | medium | src/PdfLiteViewer/App.xaml.cs:61-93 | fixed — added static lock around File.AppendAllText |
| F-A-2 | medium | src/PdfLiteViewer/PdfDoc.cs:73-103 | fixed — track SemaphoreSlim acquisition; cancel-aware finally |
| F-B-3 | low | tools/HangProbe/{Program.cs,Shots.cs,StressPdf.cs} | fixed — per-run Guid suffix on all temp files |
| F-B-4 | medium | tools/HangProbe/Program.cs:191-220 | fixed — try/finally around preview show + checks |
| F-B-6 | low | tools/HangProbe/Program.cs:127 | fixed — fail loud on non-numeric PageBox |
| F-B-7 | low | tools/HangProbe/LayoutChecks.cs:42 | fixed — explicit guard against zero-width/height |
| F-B-8 | medium | tools/StoreShots/Program.cs:123-134 | fixed — substring match + abort if missing |
| F-B-9 | low | tools/HangProbe/Program.cs:71-81 | fixed — mirror StoreShots screen-size gate |
| F-B-12 | low | tools/HangProbe/StressPdf.cs:87 | fixed — stream MemoryStream directly to file |
| F-C-4 | low | packaging/README.md winget section | fixed — added historical-disclaimer note |
| F-D-1 | low | src/PdfLiteViewer/AboutWindow.xaml:145 | fixed — bound to `{local:Loc AppTitle}` |
| F-D-3 | low | 4 CJK Strings.*.resx | fixed — half-width `:` → full-width `：` |

## Refuted by independent verification (12)

| ID | Sev | Disposition | Reason |
|---|---|---|---|
| F-A-3 | low | REFUTED | `??=` re-reads field inside lock; ExecutionAndPublication ensures single parse. Skeptic's confirmation was based on a flawed re-analysis. |
| F-B-1 | medium | REFUTED | Constructor's render goes to real renderer, not the override installed after; loop body clears pending each iteration. |
| F-B-2 | medium | REFUTED | AboutWindow ctor calls `ApplyFlowDirection(this)` which sets `FlowDirection` synchronously; no Loaded/DataTrigger involvement. |
| F-B-5 | low | REFUTED | Settle-time stalls belong to the scenario by design (documented in UiWatchdog comments). |
| F-B-10 | low | REFUTED | Case-insensitivity is exercised by table case `@"C:\gone\Moved Away.PDF"` against `EndsWith(".pdf", OrdinalIgnoreCase)`. |
| F-B-11 | low | REFUTED | RunAsync reaches `WindowChecksAsync` only via ApplicationIdle dispatcher; `CurrentDispatcher` is the UI dispatcher. |
| F-C-1 | high | REFUTED | PowerShell's `&` operator and unquoted `$var` correctly tokenize; paths with spaces are quoted by CommandLineToArgvW. Array-form "fix" would be a no-op. |
| F-C-2 | medium | REFUTED | MSIX build is Windows-only (Partner Center interactive); CI gate for it has no host. Release flow is documented as manual. |
| F-C-3 | low | REFUTED | `tools/make_icons.py` exists; `__pycache__/` rule is live. |
| F-C-5 | low | REFUTED | Only one PropertyGroup with Version exists today; uniqueness check would be defense-in-depth, not a present defect. |
| F-D-2 | medium | REFUTED | WPF mirrors logical margins; the asymmetric cited cases are either inside NoMirror or preserved under mirroring. |
| F-D-4 | low | REFUTED | PrintPreviewWindow.xaml.cs:47 sets Title from `PrintWindowTitleFormat` in ctor. |
| F-D-5 | low | ACCEPTED | `/12` suffix is sufficient visible label; ToolTip would duplicate. |

## Final skeptic pass (21 verdicts, all accept)

F-FINAL-1 (nit): wrong xmldoc cref in PdfDoc.cs:230 — pre-existing, runtime correct.
F-FINAL-2 (low): _printCts leak on window close without 2nd print — pre-existing pattern.
F-FINAL-3 (low): `acquired` flag technically redundant — defensive code, harmless.
F-FINAL-4..12 (low): each per-file correctness check — all accept.
F-FINAL-13..17 (low): refutation spot-checks — all accept (controller correct).
F-FINAL-18 (low): cross-thread INPC in OpenFileAsync/LoadChaptersAsync — already on UI thread.
F-FINAL-19 (low): RenderPageSync intra-render cancellation gap — out of scope (PDFtoImage has no hook).
F-FINAL-20 (low): StoreShots printer substring match broader than exact — documented intent.
F-FINAL-21 (low): diff cleanliness — all 17 files in scope, no secrets, no unrelated noise.

## Validation evidence
- `dotnet build PdfLiteViewer.slnx -c Release` — 0 warnings, 0 errors
- `dotnet run --project tools\ChapterSmoke -- tools\fixtures\*.pdf` — all 6 fixtures pass
- 3 commits land cleanly on review branch (no force-push, no history rewrite)