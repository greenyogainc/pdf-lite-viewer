# Full Codebase Review — PDF Lite Viewer (2026-09-26)

**Repository**: greenyogainc/pdf-lite-viewer
**Target branch**: `main`
**Base SHA**: `4a279c972d6aaebea5a4b05dcca7de146d99ca9d` (main tip: 1.0.17 release + winget 1.0.17 manifests)
**Review branch**: `code-review/full-codebase-review-20260926-1016`
**Reviewed range**: `4a279c9..HEAD` (6 fix commits + this report)

---

## Outcome

**Status: INCOMPLETE — pending the Windows-native validation run.**
Every confirmed actionable finding is fixed, and the final independent Opus challenge returned
CLEAN. The native checks — `dotnet build`, ChapterSmoke and HangProbe — could not run in the
review environment. It is a Linux container whose egress policy blocks nuget.org and the .NET
SDK feeds, and WPF plus HangProbe need a Windows desktop anyway. The branch is ready for that
run, and nothing else stands between it and `READY FOR MERGE`. See [Validation](#validation).

- 11 candidate findings from three passes:
  - 9 fixed across 6 commits and 6 files (+151/−89).
  - 1 refuted (B-3).
  - 1 accepted as historical documentation (F-3).
- One owner decision was taken: **A-1, restore ARCH-05**. A print job that has been sent survives the preview window closing.
- One HangProbe regression check was added for the rotated-render bug (F-1).

## Initial state and isolation

- The clone was fresh, on `main`, clean, with upstream `origin/main`, at `4a279c9`.
- The branch was created with `scripts/init-review-branch.sh main` from the full-codebase-review skill.
  - No worktree was needed because the tree was clean.
  - The branch's upstream was unset right after creation, so a bare `git push` can never target `main`.
- There was no force-push and no history rewrite, and `main` was not touched.

## Stack and coverage

- **Stack:** WPF on .NET 10 (`net10.0-windows10.0.19041.0`), C# with nullable enabled.
  - PDFtoImage 5.1.0 (PDFium) renders.
  - PdfPig 0.1.15 reads outlines.
  - WebView2 1.0.4129.50 (pinned exact) hosts the support form.
- **Tracked files:** 106 in total.

| Partition | Reviewer | Files |
|---|---|---|
| A: app logic, architecture, concurrency | Opus | 13 `src/PdfLiteViewer` code files (App, AssemblyInfo, ChapterItem, Loc, MainWindow, PageItem, PdfDoc, PdfPrintPaginator, PrintJob, PrintPreviewWindow, AboutWindow, SupportNavigationPolicy, csproj) |
| B: harnesses and tooling | Sonnet | 19: HangProbe (8 .cs + csproj), ChapterSmoke (2), StoreShots (3 .cs + csproj), make_icons.py, MakeChapterFixtures.ps1, fixture contents checked with `strings` |
| C: packaging, release, repo config, docs | Sonnet | 21: 3 packaging scripts, appxmanifest, packaging/README, captions.md, winget 1.0.17 (and 1.0.16 as the drift baseline), slnx, .gitignore, .gitattributes, README, CLAUDE.md, LICENSE, csproj files |
| D: XAML and i18n | Sonnet | 19: 4 XAML files, Loc.cs, Strings.resx and 13 satellites (mechanical checks: key parity, reference audit, placeholder parity, well-formedness) |
| Skeptic (pass 1), challengers (passes 2 and 3) | Opus | every candidate, every fix commit, and a re-pass over `src/PdfLiteViewer/*.cs` and `tools/HangProbe/*.cs` |

- **Excluded as binary:** 26 files (`*.png`, `*.ico`, fixture and demo `*.pdf`). The scripts and code that generate them were reviewed.
- **Excluded as published history:** winget 1.0.13–1.0.15. These were read for consistency only, because they are immutable once merged upstream.
- **Excluded as tool state:** `.varmem/*`.
- **Excluded as docs history:** earlier review reports.

## Baseline validation

| Check | Result |
|---|---|
| `dotnet build PdfLiteViewer.slnx -c Release` | **Not run here.** No .NET SDK, and the egress policy blocks `api.nuget.org`, `dot.net`, `builds.dotnet.microsoft.com` and `packages.microsoft.com`. |
| `dotnet run --project tools\ChapterSmoke -- tools\fixtures\*.pdf` | Not run (same reason) |
| `dotnet run --project tools\HangProbe` | Not run: needs an interactive Windows desktop |
| C# syntax parse of all 24 `.cs` files (tree-sitter-c-sharp) | 0 errors at base, and 0 errors after every commit |
| resx mechanical checks (partition D) | 81 keys × 14 locales, full parity; placeholders match; all well-formed |

The last recorded native baseline is on `main` (2026-09-23 report): 0 warnings and 0 errors, and ChapterSmoke 6/6.

## Findings and disposition

| ID | Sev | Location | Disposition |
|---|---|---|---|
| A-1 | medium | PrintPreviewWindow.xaml.cs `OnClosing` | **Fixed** (f089ef3). Owner chose to restore ARCH-05. |
| A-2 | low | MainWindow.xaml.cs `UpdateRenderedPagesAsync` | **Fixed** (7bc4500) |
| A-3 | low | PdfPrintPaginator.cs `GetPage` | **Fixed** (f089ef3, completed by 5427980) |
| S-1 | low | PdfPrintPaginator.cs `GetPage` | **Fixed** (f089ef3) |
| B-1 | low (raised as medium) | tools/HangProbe/Program.cs | **Fixed** (7254d60) |
| B-2 | nit (raised as medium) | tools/HangProbe/Program.cs | Refuted as a defect (no impact: `Main` shuts the app down). Structure aligned with its comment in 7254d60. |
| B-3 | info (raised as low) | tools/MakeChapterFixtures.ps1:14-15 | **Refuted.** The guard protects the offset invariant it names. The script is pure ASCII today. |
| F-1 | medium | PdfDoc.cs render options | **Fixed** (5427980) + HangProbe regression check |
| F-2 | low | PrintPreviewWindow.xaml.cs cursor | **Fixed** (74ed03e) |
| F-3 | nit | README 1.0.17 entry; 2026-09-23 report | **Accepted** as shipped history. Record at the next version bump (see Follow-ups). |
| G-1 | low | MainWindow.xaml.cs screen render cap | **Fixed** (5b6271b) |

Partitions C and D returned zero findings.

- **Partition C** checked:
  - version agreement between the csproj, appxmanifest, winget, README and tag;
  - installer URLs, SHA256 format, `NestedInstallerFiles` against the real zip layout, architectures, and MinimumOSVersion;
  - `$LASTEXITCODE` handling in every script.
- **Partition D** checked the 81 keys in all 14 locales: parity, placeholder sets, every reference resolved, no dead keys, and RTL/NoMirror and accessibility names.

The prior ledger item F-FINAL-2 (the `_printCts` leak) is moot, because the field is gone.

### Finding details

**A-1: closing the print preview cancelled the job (medium).**
- **Origin:** commit 08ca019 was an edit the partition-A agent made without being asked during the 2026-09-23 review. It made `OnClosing` cancel `_printCts`.
- **Why it matters:**
  - It reversed ARCH-05, which the owner accepted on 2026-09-03: a sent job survives the preview closing, on a foreground STA thread.
  - While a job spools, Cancel and Esc are disabled, so the title-bar X is the only way back to the document.
  - The paginator then threw at the next page. Depending on whether `XpsDocumentWriter` wraps the exception, the job was silently truncated or dropped (the exception was swallowed), or it showed a misleading "Printing failed" message.
- **Fix:** `OnClosing` and `_printCts` are removed, and the pre-08ca019 contract and comments are restored. `PrintJob`/`PdfPrintPaginator` keep their optional token (default `None`, no caller passes one).

**A-2: unnecessary dispatcher hop (low).**
- **Claim that was wrong:** the 08ca019 comment said the continuation after `RenderPageAsync` ran on the thread pool. It resumes on the UI thread: the only caller is a `DispatcherTimer` tick, and PdfDoc's internal `ConfigureAwait(false)` does not change the caller's context.
- **The race it caused:** the `InvokeAsync(DataBind)` hop split the `ct` check from the write. A Normal-priority `RotateClockwise` queued in between could leave an old-orientation bitmap marked as current. This was reachable from harness-driven rotation, not from user input.
- **Fix:** the page is now assigned directly after the token check.

**A-3 / S-1: print pixel budget (low).**
- **A-3:** `GetPage` capped the width at 6000 px but tested the area using the uncapped height. That shrank A1/A0 pages twice; A0 printed at about 118 DPI.
- **S-1:** tall pages under the area budget (A2, ANSI C) still went over 6000 px in height.
- **Fix:** one scale factor now enforces both limits: longest side ≤ `MaxPixelDimension` and area ≤ `MaxPixelArea`. `Floor` keeps the height derived from the aspect ratio at or below the cap.
- **Result:** A0 prints at 4243×5999 (128 DPI, the right number under a 6000 px cap). Letter is unchanged at 2550×3300.

**B-1: HangProbe temp files leaked (low).**
- **Cause:** the Guid-named stress PDF (up to about 19 MB) and the print XPS, introduced in F-B-3, were never deleted. Every run added more to `%TEMP%`, whereas the old fixed names had overwritten each other.
- **Fix:** both files are deleted once they have been used.
- **Unchanged:** the PNG captures stay, as README documents, for manual inspection.

**F-1: renders at 90°/270° had the wrong resolution (medium, pre-existing).**
- **Root cause:** PDFtoImage 5.1.0 applies `WithAspectRatio` against the unrotated page and swaps width and height afterwards (`Internals/PdfDocument.cs`, `Render`). So the width the app asked for came back as the bitmap's height.
- **Undersampling:** a rotated landscape page (792×612 pt) printed at about 232 DPI and looked soft on screen (`Stretch=Fill`).
- **Oversampling:** a rotated tall page blew past the paginator budget. A 612×7200 pt foldout produced 30000×2550 px, about 306 MB.
- **Fix:** both render paths now share `PdfDoc.RenderOptionsFor`, which requests the unrotated *height* for quarter turns. The output is then exactly the target width at the display aspect ratio.
- **Regression check:** HangProbe `print: a 90° page renders at 300 dpi across its content box`. It passes with the fix (2550×1970 for a 2550×1970 box) and fails without it (3300×2550).

**F-2: wait cursor stuck after closing mid-print (low).**
- **Cause:** restoring ARCH-05 also brought back the 1.0.16 behavior. The app-wide `Mouse.OverrideCursor` stayed on Wait until the spooler had the last page.
- **Fix:**
  - `OnClosed` now clears the cursor.
  - `SetPrintingState` only touches the cursor while the window is loaded, so a finished job cannot clear a cursor that a later preview set. The preview is modal, so a later preview only exists after this one has closed.

**G-1: screen renders capped by width only (low, pre-existing).**
- **Cause:** a strip page, or a wide banner turned a quarter, could request 3500×87500 (about 1.2 GB). Up to `KeepBuffer` such pages are kept in memory.
- **Fix:** a 24M px area budget now applies alongside the 3500 px width cap.
- **Unaffected:** ordinary pages. At the width cap, Letter is 15.9M and Legal is 20.2M.

### Correction to the 2026-09-23 report
- Its "Residual risk" bullet claims `MainWindow` marshals every PageItem update through `Dispatcher.InvokeAsync` because the continuation runs off-thread. That premise was false, and 7bc4500 removes the hop.
- Its 08ca019 description presents cancel-on-close as a fix. It was a reversal of ARCH-05, now undone.
- Both reports are left unedited as history; this section records the corrections.

## Commits

```
5b6271b  fix(render): cap on-screen page bitmaps by area as well as width
74ed03e  fix(print): release the app-wide wait cursor when the preview closes mid-print
5427980  fix(render): size 90/270-degree renders by the rotated width
7254d60  fix(hangprobe): delete per-run temp PDF/XPS and guard the preview open
7bc4500  fix(render): assign rendered page on the UI thread without a dispatcher hop
f089ef3  fix(print): restore ARCH-05 print-survives-close and fix large-page pixel budget
```

Changed files:
- `src/PdfLiteViewer/{MainWindow.xaml.cs, PdfDoc.cs, PdfPrintPaginator.cs, PrintPreviewWindow.xaml.cs}`
- `tools/HangProbe/{ContractChecks.cs, Program.cs}`

No XAML, resx, packaging or version files were touched.

## Validation

**Done in the review environment:**
- tree-sitter C# parse after each commit: 24/24 files, 0 syntax errors.
- Compile-level read-through by two independent Opus challengers. They checked types, target-typed conditionals, removed members and usings, and nullable flow. No issues found.
- The PDFtoImage 5.1.0 source (tag `v5.1.0`) was traced to confirm the F-1 root cause and the new sizing, including dpi correction, tiling and int truncation.
- The new sizing math was checked numerically for Letter, A2, A0, ANSI C and extreme strips.

**Required on Windows before merge** (the remaining gate):

```powershell
git fetch origin code-review/full-codebase-review-20260926-1016
git switch code-review/full-codebase-review-20260926-1016
dotnet build PdfLiteViewer.slnx -c Release          # expect 0 warnings, 0 errors
dotnet run --project tools\ChapterSmoke -- tools\fixtures\*.pdf   # expect 6/6 ok
dotnet run --project tools\HangProbe                 # expect all checks pass, incl. the new 90° print check
```

**Manual smoke tests:**
1. Rotate a landscape page 90° and check that it is sharp on screen.
2. Print about 50 pages to Microsoft Print to PDF, close the preview mid-job, and confirm that the cursor returns immediately and the output PDF has every page.

## Convergence history

- **Pass 1:** four partitioned reviewers produced 7 candidates (A:3, B:3, C:0, D:0). The skeptic added S-1 and confirmed 5 (A-1, A-2, A-3, S-1, B-1). It refuted B-2 as a defect and refuted B-3. A-1 went to the owner, who chose to restore ARCH-05. Fixed in 3 commits.
- **Pass 2:** the independent challenger rejected the A-3 fix as incomplete under rotation (F-1). It also found the F-2 regression brought back by A-1, and the F-3 doc nit. Fixed in 2 commits.
- **Pass 3:** the independent challenger accepted every commit. It raised G-1 (advisory). G-1 was fixed in 1 commit and then challenged on its own: ACCEPT. Verdict: **CLEAN**.

## Remote branch, merge proposal, pipeline

- **Remote branch:** `code-review/full-codebase-review-20260926-1016`, pushed without force.
- **Merge proposal:** see the PR against `main`. Its URL and final-SHA evidence are recorded in the PR itself and in the session response. This report cannot record its own final SHA without creating another commit.
- **Pipeline: NO PIPELINE CONFIGURED.** The repo has no `.github/workflows`, `.gitlab-ci.yml` or `azure-pipelines.yml`, and the GitHub Actions API for this repo reports `total_count: 0` workflows. This is reported as not configured, not as passing.

## Rollback

- To undo individual fixes on the review branch, revert the commits in reverse order: `git revert 5b6271b 74ed03e 5427980 7254d60 7bc4500 f089ef3`.
- Every commit is self-contained. A-1 (f089ef3) and F-2 (74ed03e) go together: F-2 depends on the ARCH-05 restore.
- `main` is untouched.

## Residual risk and follow-ups

- **Native validation pending** (see above). This is the one thing standing between this branch and READY FOR MERGE.
- **Next version bump:** per CLAUDE.md, the 'What's new' entry must go in the same commit as the bump. It should record:
  - closing the print preview no longer cancels a sent job (reverting the 1.0.17 note);
  - quarter-turned pages render at full resolution on screen and paper;
  - large-format prints keep more resolution;
  - very tall pages no longer allocate huge bitmaps.
- **No CI:** release verification is still the manual `Verify-Release.ps1` run. This is unchanged from the previous review and still the main operational gap.
- **Review process:** every reviewer this run was dispatched as a read-only agent type (no Edit/Write tools). This is what the 2026-09-23 report recommended after A-1's root cause, an unrequested edit by a reviewer.
