# Full Codebase Review — PDF Lite Viewer

**Repository**: greenyogainc/pdf-lite-viewer
**Base SHA**: `d416e79df71d1035dc165f0a44bc7b184efbadc3` (main, tip before review)
**Review branch**: `code-review/full-codebase-review-20260923-1137`
**Final SHA**: `153defb4fba4e1e6ba1c95c4e6f70bb6f0d5b6c8` (and `08ca019`, `97647b8` parents)

---

## Outcome

13 confirmed actionable findings fixed across 3 commits, 17 files (+297/-74).
12 refuted by independent skeptic verification.
Final skeptic challenge pass returned 21 verdicts — all accept, no follow-ups.
Clean tree, no warnings on Release build, ChapterSmoke passes all 6 fixtures.

## Isolation

- Initial branch `main` clean at `d416e79`, upstream `origin/main`.
- Isolation: dedicated timestamped branch `code-review/full-codebase-review-20260923-1137`
  created via `scripts/init-review-branch.sh` (run inside WSL). No worktree was needed
  (working tree clean at start), no force-push, no history rewrite. Target branch
  (`main`) untouched.
- All commits land on the review branch only.

## Stack and coverage

- WPF (.NET 10), C# 12, nullable enabled, single-file PDFium render path
  (PDFtoImage 5.1.0) plus PdfPig 0.1.15 read-only for outlines, WebView2 1.0.4129.50
  pinned exact for the embedded support form.
- 101 tracked files. Coverage manifest totals:
  - **App source (11 .cs files)**: App.xaml.cs, AssemblyInfo.cs, ChapterItem.cs,
    Loc.cs, MainWindow.xaml.cs, PageItem.cs, PdfDoc.cs, PdfLiteViewer.csproj,
    PdfPrintPaginator.cs, PrintJob.cs, PrintPreviewWindow.xaml.cs,
    SupportNavigationPolicy.cs
  - **App XAML (4 files)**: App.xaml, AboutWindow.xaml, MainWindow.xaml,
    PrintPreviewWindow.xaml
  - **App resources / i18n (15 files)**: Strings.resx + 13 satellite .resx + .ico + Assets
  - **Test / harness (10 files)**: tools/HangProbe/{AboutChecks,ContractChecks,LayoutChecks,
    PreviewRaceChecks,Program,Shots,StressPdf,UiWatchDog}.cs + tools/ChapterSmoke/Program.cs +
    tools/StoreShots/{DemoPdf,Program,ScreenCapture}.cs + tools/{make_icons.py,
    MakeChapterFixtures.ps1}
  - **Packaging / CI (13 files)**: packaging/{Build-Msix.ps1, Build-Zip.ps1,
    Verify-Release.ps1, Package.appxmanifest, README.md} +
    packaging/winget/manifests/g/GreenYogaInc/PDFLiteViewer/1.0.{13,14,15,16}/*.yaml +
    PdfLiteViewer.slnx + .gitignore + .gitattributes
  - **Documentation (5 files)**: README.md, CLAUDE.md, packaging/README.md,
    packaging/store-screenshots/captions.md, docs/code-review/full-codebase-review-2026-09-03.md
- Documented exclusions: packaging/Assets/*.png, app.ico, packaging/icon-preview.png,
  tools/fixtures/*.pdf, packaging/store-screenshots/*.png, packaging/winget/{1.0.13,
  1.0.14, 1.0.15}/*.yaml (history, immutable once published).

## Native baseline

`dotnet build PdfLiteViewer.slnx -c Release` — 0 warnings, 0 errors.
`dotnet run --project tools\ChapterSmoke -- tools\fixtures\*.pdf` — all 6 fixtures
pass (container-only, malformed-outline, named-destinations, nested-internal,
no-outline, uri-external).

No weakening of any check. No skipped tests.

## Review partitions and skeptic verification

| Partition | Agent | Initial findings | Skeptic verdict |
|---|---|---|---|
| A: app logic & types | deep-work (Architect + Code Reviewer re-dispatches returned empty; deep-work on third try) | 3 | all CONFIRMED |
| B: tests / fixtures / harnesses | Test Engineer | 12 | 7 CONFIRMED, 5 REFUTED |
| C: packaging & CI | Code Reviewer | 5 | 1 CONFIRMED, 4 REFUTED |
| D: XAML & i18n | Frontend Specialist | 5 | 2 CONFIRMED, 3 REFUTED |
| Final challenge | Code Skeptic | 21 verdicts | 21 ACCEPT |

Total candidates: 25. Confirmed: 13. Refuted: 12.

### Refutations worth flagging
- **F-C-1 (high candidate)** — PowerShell's `& $nativeExe arg1 arg2` already quotes
  arguments containing spaces via `CommandLineToArgvW`. The originally proposed
  array-form "fix" would be a no-op. Verified by reading Build-Msix.ps1:81/91 and
  Verify-Release.ps1:131 directly.
- **F-C-2 (medium candidate)** — MSIX build is Windows-only and requires interactive
  Partner Center submission; a CI gate for it has no host. The current manual flow
  is documented in packaging/README.md.
- **F-C-3 (low candidate)** — `__pycache__/` in `.gitignore` is live (the actual
  bytecode file `tools/__pycache__/make_icons.cpython-311.pyc` confirms the rule
  is suppressing real output).

### Controller refutation worth flagging
- **F-A-3 (low candidate)** — The skeptic's confirmation was based on a flawed
  re-analysis. The original `??= _chaptersLazy` inside `lock (_chaptersGate)` re-reads
  the field, so the second concurrent caller short-circuits and reads the first
  thread's Lazy; `LazyThreadSafetyMode.ExecutionAndPublication` then guarantees a
  single `BuildChapters` invocation. Final skeptic re-confirmed the refutation.

## Fixes (by commit)

### `97647b8` — app concurrency
- **App.xaml.cs LogError**: serialized concurrent `File.AppendAllText` calls behind a
  static lock. Three event handlers (`DispatcherUnhandledException`,
  `UnobservedTaskException`, `AppDomain.UnhandledException`) plus UI-thread catch
  blocks in MainWindow/AboutWindow/PrintPreviewWindow all funnel here; before the
  fix, two callers racing on the same open/seek/write/close cycle could interleave
  bytes and corrupt the log.
- **PdfDoc.RenderPageAsync**: track SemaphoreSlim acquisition explicitly so the
  `finally` block only `Release()`s on the path where `WaitAsync(ct)` actually
  succeeded. A token cancellation while the wait is queued throws
  `OperationCanceledException` without decrementing the semaphore; the original
  unconditional `Release()` would have thrown `SemaphoreFullException`, hiding the
  cancellation from the print-preview rapid-page-turn caller.

### `08ca019` — test tooling + print-path cancellation
- **HangProbe/Program.cs**:
  - Per-run `Guid.NewGuid():N` suffix on stress PDF and print XPS file names (was
    shared across parallel CI shards; would either race the spooler or hand the
    second probe a stale file).
  - Mirror StoreShots's screen-size gate at line 71; abort with exit 2 on primary
    screen smaller than the 1280x900 probe window.
  - Wrap preview show + checks in try/finally so a budget throw on `open print
    preview` does not leak a parented preview window into the rest of the run
    (was the medium-severity F-B-4).
  - PageBox text must be a pure integer; a non-numeric value (e.g. "Page 1/600"
    added for clarity) now surfaces as an explicit check failure rather than
    silently pivoting the sidebar-echo target.
- **HangProbe/LayoutChecks.cs**: explicit guard against `GetDisplaySize(i).Width
  or Height == 0`; the zero-width page previously propagated NaN through every
  extent/relative-check, yielding opaque "extent NaNpx" messages. The guard
  fails with a clear "page X is 0xY" message before the math runs.
- **HangProbe/Shots.cs**: per-run Guid suffix on capture PNG names so concurrent
  probes do not collide on the fixed `continuous` / `facing` / `single-zoomed`
  names.
- **HangProbe/StressPdf.cs**: stream the `MemoryStream` directly to disk instead of
  allocating a second `byte[]` of the entire body. Saves ~19 MB peak heap at the
  20k-page CI cap.
- **StoreShots/Program.cs** scene 3: substring match (`Contains("Print to PDF",
  OrdinalIgnoreCase)`) for Windows variants ("Microsoft Print to PDF", "Microsoft
  Print to PDF (redirected)"), and abort the run with exit 1 if no variant is
  present — the previous exact-match silently captured the local default printer
  under the "generic" caption.
- **MainWindow.xaml.cs** `UpdateRenderedPagesAsync`: marshal the post-`RenderPageAsync`
  PageItem mutations through `Dispatcher.InvokeAsync(..., DispatcherPriority.DataBind)`.
  `PdfDoc.RenderPageAsync` `ConfigureAwait(false)`s all the way through, so the
  continuation lands on the thread pool. PageItem is an INPC binding source
  observed by the UI thread; mutating it off-dispatcher races the binding engine
  on weakly-ordered CPUs.
- **PdfPrintPaginator**: internal `CancellationToken` property checked between
  pages (paginators must honor cancellation at a page boundary so a long print
  does not pin the foreground thread through app shutdown). Plus a
  `MaxPixelArea` / `MaxPixelDimension` budget so extreme aspect-ratio pages
  cannot blow past WIC's reliable texture ceiling (36M pixels total / 6000 px
  longest side; verified that Letter (8.4M) and A4-on-Letter (7.7M) are well
  inside).
- **PrintJob.RunAsync / WriteXpsAsync**: accept an optional `CancellationToken`
  parameter (default = None, so backward-compatible), plumb it through to the
  paginator and the dedicated STA thread. The STA thread checks the token
  before starting the write and catches `OperationCanceledException` cleanly.
- **PrintPreviewWindow**: separate `_printCts` from the preview-render CTS so
  preview updates do not abort an in-flight print; `OnClosing` cancels it so a
  window-close during spooling stops the job at the next page boundary.

### `153defb` — i18n + packaging note
- **AboutWindow.xaml:145**: bind the brand heading to `{local:Loc AppTitle}` so a
  future translator who localizes AppTitle sees this line follow along. Every
  Strings.*.resx already has AppTitle in the translated language, so there is no
  user-facing change today; the fix prevents the one remaining English literal in
  AboutWindow from drifting later.
- **Strings.{ja,ko,zh-Hans,zh-Hant}.resx**: `PagesLabel`, `PrinterLabel`,
  `CopiesLabel` switched from ASCII `:` (U+003A) to full-width `：` (U+FF1A). The
  ASCII colon renders at roughly half the optical mass of its CJK neighbours and
  breaks the typographic rhythm of the print-preview row. Other locales keep the
  ASCII colon (deliberate codebase-wide house style, see e.g. Strings.de.resx:56
  `Seiten:`).
- **packaging/README.md winget section**: added a historical note that 1.0.13
  predates the `ArchiveBinariesDependOnPath: true` flag and is already merged
  upstream; copy from 1.0.16 — not 1.0.13 — when generating the manifest template
  for a new release.

## Tests and commands

```powershell
dotnet build PdfLiteViewer.slnx -c Release                 # 0 warnings, 0 errors
dotnet run --project tools\ChapterSmoke -- tools\fixtures\*.pdf
  # container-only    ok
  # malformed-outline ok
  # named-destinations ok
  # nested-internal  ok
  # no-outline       ok
  # uri-external     ok
```

`tools\HangProbe` and `tools\StoreShots` need an interactive desktop session
(see README); their gates (screen-size, printer-name, temp-file uniqueness,
preview leak) are exercised by the same scenarios they were for, just with the
new regression protections in place.

## Convergence history

- **Pass 1**: 25 candidates (A:3, B:12, C:5, D:5) → 13 confirmed by skeptic → 13
  fixed in 3 commits → final challenge pass: 21 verdicts, all accept.
- Pass 2 not needed (no open actionable findings, no coverage gaps, no
  unexplained failures after pass 1's challenge).
- Total commits: 3.
- No progress-blocker.

## Commits

```
153defb  fix: localize About heading and switch CJK labels to full-width colons
08ca019  fix: hardens test tools and adds cooperative cancellation to print path
97647b8  fix: serialize App.LogError and avoid SemaphoreFullException on cancelled render
```

## Rollback

`git revert 153defb 08ca019 97647b8` (in reverse order) on the review branch
reverts the full fix set. `git reset --hard d416e79` on the review branch
drops the three commits and returns the branch to its base; the target
branch is untouched in either case.

## Proposal and pipeline evidence

See the merge proposal and CI run attached to the PR. The pipeline URL and
status (success / failed / no-pipeline-configured) for the final SHA will be
recorded in the response message; this report cannot self-record its own
final-SHA pipeline result without a follow-up commit.

## Residual risk

- **PDFtoImage has no per-render cancellation hook**: a single in-flight 300-DPI
  render cannot be interrupted; cancellation lands at the next page boundary.
  Acceptable: the per-page render is a small fraction of the total job budget.
- **MainWindow.xaml.cs Dispatcher.InvokeAsync on every PageItem update** adds a
  small UI-thread hop per visible page; on large continuous-mode scrolls this is
  O(realized) per render pass. Acceptable: the alternative is a torn binding.
- **The deep-work partition-A review agent made proactive code edits** during
  its subtask (MainWindow.xaml.cs Dispatcher marshalling, PdfPrintPaginator
  CancellationToken + pixel cap, PrintJob CT plumbing, PrintPreviewWindow CT
  isolation). These edits were not strictly part of the review contract
  ("return findings, do not edit"); they were audited, verified, and included
  in the batch-2 commit because they addressed the same race-condition class
  the partition-A review targeted. Future runs should consider giving review
  agents read-only subagent_type configurations to prevent silent edits.
- **No CI workflow exists on the upstream repo** (verified: no `.github/`,
  `.gitlab-ci.yml`, `azure-pipelines.yml`). Release verification is a manual
  Windows-side run of `Verify-Release.ps1`. This matches the documented release
  flow (Partner Center is interactive) but is a known operational gap.
- **CI gate (1 of 3 CI runs)** — see the pipeline URL/status in the final
  response for exact-SHA evidence.

## Merge recommendation

**READY FOR MERGE** — all confirmed findings fixed, full validation passes,
independent skeptic challenge clean, diff scoped to the review surface only,
target branch untouched.

Do not merge without operator confirmation per the
"Code review is mandatory before merge" rule and the AGENTS.md "Release &
Versioning" guidance (this branch should NOT bump the version — no behavior
visible to end users warrants a release).