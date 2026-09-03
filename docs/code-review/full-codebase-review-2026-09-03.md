# Full codebase review and repair — 2026-09-03

## Executive summary

Terminal status: **INCOMPLETE — NO PIPELINE CONFIGURED** (see *Pipeline*). Every other
completion criterion is met: the whole tracked tree was reviewed twice by independent
agents plus an architecture review and an independent final challenge, every confirmed
actionable finding was fixed on an isolated branch with regression checks added to the
repository's own harnesses, the repository-native validation passes at the final code
commit, and pull request #3 targeting `main` proposes the merge without performing it. The
repository has no CI provider, so no pipeline can vouch for the final SHA; the local
validation recorded below is the only automated evidence available.

Thirty-eight candidate findings were raised across the passes; 34 were confirmed and fixed
(2 high, 4 medium, 28 low), 2 were refuted with evidence, and 2 were accepted as deliberate,
documented behaviour. No critical findings. The two high findings were a data race in the
print pipeline (a mid-job view rotation changes the sheets still being produced) and a
regression introduced by a first-round fix, caught by the architecture review before
anything was pushed.

## Scope and setup

| Item | Value |
|---|---|
| Repository | `greenyogainc/pdf-lite-viewer` (local root `C:\Users\andreab\pdf-viewer`) |
| Target branch | `main` (remote default; unprotected, no rulesets) |
| Base SHA | `5058422` (`docs: add CLAUDE.md with Release & Versioning policy`) |
| Review branch | `code-review/full-codebase-review-20260903-0809` |
| Final code commit | `eaf2c16` (this report is committed on top of it) |
| Reviewed commit range | `5058422..eaf2c16` |
| Initial Git state | branch `main`, clean worktree, in sync with `origin/main`, no stashes, single worktree |
| Isolation | Worktree was clean, so the review branch was created in place from `main`; `main` was never modified |
| Toolchain | .NET SDK 10.0.111, Windows PowerShell 5.1 and PowerShell 7, Windows SDK `makeappx`, Python 3.11 (verification scripts only) |

Languages and components reviewed: C# 13 / WPF (`src/PdfLiteViewer`, the shipped app), the
three C# harness tools (`tools/HangProbe`, `tools/ChapterSmoke`, `tools/StoreShots`),
PowerShell packaging and fixture scripts, the MSIX manifest, winget manifests, resx
localization (14 languages), Python icon generator, repository documentation and policy files.

Policy files read before acting: `CLAUDE.md` (repo), `C:\Users\andreab\CLAUDE.md` and
`AGENTS.md` (cross-project), `~/.claude/CLAUDE.md`, `README.md`, `packaging/README.md`.

## Coverage manifest

`git ls-files` listed 96 tracked files at the base commit and 97 at the final code commit
(`tools/HangProbe/ContractChecks.cs` was added by this review and re-reviewed in pass 2 and
the final challenge). This report is the 98th.

| Class | Files (at `eaf2c16`) | Lines (at base) | Reviewer (pass 1 / pass 2) |
|---|---|---|---|
| P1 — core viewer (`MainWindow.*`, `PdfDoc`, `PageItem`, `ChapterItem`, `App.*`, `Loc`, `AssemblyInfo`) | 9 | 2 029 | Sonnet / Sonnet |
| P2 — printing, About/WebView2, navigation policy, 14 resx, csproj, slnx | 23 | 2 752 | Sonnet / Sonnet |
| P3 — packaging scripts, MSIX manifest, winget manifests, docs, policy, gitignore/gitattributes, varmem, icon and fixture generators | 24 | 1 264 | Sonnet / Sonnet |
| P4 — harness tools (`HangProbe` incl. `ContractChecks.cs`, `ChapterSmoke`, `StoreShots`) and fixture contract | 15 | 1 946 | Sonnet / Sonnet |
| Architecture / cross-cutting (whole `src/`, `HangProbe` contracts, `Verify-Release.ps1`) | — | — | Opus (pass 1) / Opus final challenge |
| Excluded binaries: 6 tile PNGs, icon preview, 9 Store screenshots, demo PDF, 2 brand marks, `app.ico`, 6 fixture PDFs | 26 | — | Content not reviewed; their generators (`tools/make_icons.py`, `tools/StoreShots`, `tools/MakeChapterFixtures.ps1`) were, and the fixtures were verified byte-for-byte after regeneration |

Total: 71 reviewable files fully read in pass 2 (70 in pass 1, before the new file existed);
26 binary files documented as excluded with their generators reviewed. No sampling.

## Baseline validation (before any change)

| Command | Result |
|---|---|
| `dotnet build PdfLiteViewer.slnx -c Release` | Build succeeded, 0 warnings, 0 errors |
| `dotnet run --project tools/ChapterSmoke -- 'tools/fixtures/*.pdf'` | exit 0 (6 fixtures dumped; the tool asserted nothing beyond "no exception") |
| `dotnet run --project tools/HangProbe -- 3000` | `PASS — 42 checks, no UI-thread stall over budget` |
| GitHub Actions workflows (`gh api repos/.../actions/workflows`) | `total_count: 0`; no `.github/`, no other CI configuration tracked |

## Findings ledger

Severity columns show the severity as reported by the reviewer and as verified by the
controller (every candidate was independently re-derived from the code or reproduced before
acceptance). Disposition values: fixed, invalid (refuted with evidence), accepted (intentional
and documented; no change). IDs: `P<n>-` pass-1 partitions, `ARCH-` architecture review,
`P4-2-` pass-2, `FINAL-` independent final challenge.

| ID | Reported | Verified | Location | Failure (verified) | Disposition | Repair / proof |
|---|---|---|---|---|---|---|
| P1-01 | medium | low | MainWindow.xaml.cs GoToPage | Facing mode: jumping to the other page of the visible spread rebuilt both PageItems, dropping rendered bitmaps (visible flash) | fixed | Compare facing groups before rebuilding; HangProbe "facing: jump within the spread keeps the pages" + inverse check |
| P1-02 | medium | medium | PdfDoc.cs ctor; MainWindow FitZoom/GoToPage | PDFium accepts a well-formed zero-page PDF (verified: GetPageCount=0); RebuildItems indexed empty PageSizes inside a discarded task, then every navigation key threw from Math.Clamp(page,0,-1) | fixed | PdfDoc throws UnreadablePagesException at open, routed to the existing "could not open" dialog; HangProbe "zero-page document is rejected at open" |
| P1-03 | low | low | App.xaml.cs OnStartup | A startup path that fails File.Exists was dropped silently: app started empty after double-clicking a PDF that had moved | fixed | `.pdf` paths are taken even when missing; OpenFileAsync reports them (see ARCH-01 for the corrected rule) |
| P2-01 | high | n/a | PrintPreviewWindow.xaml.cs Print_Click catch | Claimed MessageBox.Show with a closed owner throws | invalid | Verified empirically with a WPF probe: handle is 0, MessageBox falls back to the active window, returns OK, no exception |
| P2-02 | medium | low | PrintPreviewWindow Cancel_Click / Escape | Cancel and Escape stayed live while a job spooled; the window closed and the job kept printing, contradicting the documented "stops taking new input" | fixed | CancelBtn and Escape disabled while _printing; title-bar close unchanged; HangProbe "print: settings and Cancel lock while the job spools" / "controls return" |
| P2-03 | medium | low | PrintJob.cs EnumerateQueues / ResolveQueue | PrintQueue objects (spooler handles) from GetPrintQueues and GetDefaultPrintQueue never disposed | fixed | Dispose each queue after reading its name; dispose non-matching queues in the fallback |
| P2-04 | low | low | PdfPrintPaginator.cs | Unused constructor overload (no callers in src/ or tools/) | fixed | Removed |
| P2-05 | low | n/a | PrintPreviewWindow.xaml.cs LoadPrintersAsync guard | Claimed inverted guard | invalid | `!IsLoaded && !IsVisible` is exactly the closed-window state for its single call site; no failing input |
| P2-06 | low | low | PrintPreviewWindow.xaml.cs SetPrintingState doc comment | Comment contradicted behaviour | fixed | Rewritten with P2-02 |
| P2-07 | low | low | tools/HangProbe | No coverage of ParseRange, PlacePage, print commit | fixed | ContractChecks: 11 parser cases, placement check, commit checks |
| P3-01 | medium | low | Verify-Release.ps1 -WingetCheck | Only URL strings were matched; a wrong InstallerSha256 passed | fixed | Per-architecture hash compared with the local zip verified earlier; proven against real 1.0.15 artifacts (PASS as shipped, FAIL on one flipped hex digit) |
| P3-02 | medium | medium | tools/MakeChapterFixtures.ps1, tools/fixtures/*.pdf | Every xref offset wrong (verified by script), startxref wrong, /Length 44 for 38-byte streams; parsers only opened them via recovery | fixed | Generator computes xref/trailer/startxref from written bytes; fixtures regenerated and verified offset-for-offset; ChapterSmoke asserts unchanged outline shapes |
| P3-03 | low | low | Verify-Release.ps1:98 | Em dash in a script comment (repo rule: ASCII-only scripts) | fixed | Replaced |
| P3-04 | low | low | MakeChapterFixtures.ps1:2 | Em dash in a script comment, file has no BOM | fixed | Replaced |
| P3-05 | low | low | README.md / CLAUDE.md policy | Policy requires a "What's new" entry per bump; no such section existed | fixed | Section added with 1.0.13-1.0.15 entries from tagged history |
| P3-06 | low | low | Build-Msix.ps1 signtool lookup | Null signtool produced an opaque invocation error | fixed | Not-found guard mirroring makeappx |
| P3-07 | low | low | Build-Msix.ps1 / Build-Zip.ps1 -Rid | Any non-arm64 RID string silently stamped x64 | fixed | ValidateSet on -Rid in both scripts |
| P3-08 | low | low | Verify-Release.ps1 unpack | Failed unpack left plv-verify-* temp directory | fixed | makeappx call moved inside try/finally |
| P4-01 | high | medium | tools/StoreShots Program.cs Capture / VerifyAndWriteCaptions | Size check could not fail (bitmap allocated at the checked size); documented gate was vacuous | fixed | Window device-pixel coverage check before each capture; distinct-colour content check on each frame; validated with full 9-scene runs |
| P4-02 | medium | low | tools/HangProbe/UiWatchdog.cs | Task.Wait throws AggregateException, never the caught TaskCanceledException; unreachable with current shutdown order | fixed | Catch AggregateException wrapping OperationCanceledException |
| P4-03 | medium | n/a | tools/HangProbe/Program.cs PrintPreviewChecks | "printer list populated" auto-passes with a "skipped" note when no printers exist | accepted | Intentional and visible in the detail text; cannot be tested without a printer; 6 printers on this machine so the check is live here |
| P4-04 | low | low | tools/ChapterSmoke/Program.cs | Fixtures had no expectations; only exceptions failed the run | fixed | Per-fixture (pages, roots, navigable pages) assertions |
| P4-05 | low | low | tools/StoreShots Program.cs scene 7 failure path | Stale captions.md left beside partial new captures | fixed | captions.md deleted with the stale PNGs |
| ARCH-01 | high | high | App.xaml.cs OnStartup (regression introduced by the P1-03 fix in pass 1) | HangProbe and StoreShots run the production App with "3000" or an output directory as argv[0]; the widened predicate turned that into a startup file and a modal "could not open" dialog over the probe | fixed | Predicate narrowed to existing files or .pdf paths (`App.IsDocumentArgument`); HangProbe "startup argument rule" (9 cases) and "non-PDF startup arguments are ignored" |
| ARCH-02 | high | high | PdfDoc.Rotation read by PdfPrintPaginator on the print thread | Close the preview mid-job (title bar; Cancel before pass 1), press Ctrl+R: remaining sheets print rotated, and one sheet can tear between GetDisplaySize and RenderPageSync | fixed | Rotation snapshotted on the UI thread when the job starts and passed through the paginator to explicit GetDisplaySize/RenderPageSync overloads; HangProbe "print: pages keep the rotation the job started with" (content box and bitmap checked on an STA thread after a mid-job rotation) |
| ARCH-03 | medium | medium | Verify-Release.ps1 -WingetCheck hash loop (introduced by the P3-01 fix in pass 1) | A manifest with no parseable InstallerSha256 lines matched nothing and still passed | fixed | Both architectures must have been compared; proven: removing the hash lines fails with two messages, intact manifest passes |
| ARCH-04 | medium | low | PdfDoc ctor; MainWindow.OpenFileAsync post-load block | PageSizes.Count vs PageCount never asserted; an exception after `_doc = doc` vanished in the discarded task and left _doc/_items describing different documents (trigger not reproducible: PDFium kept both consistent for /Count too high and too low) | fixed | Invariant guard in the ctor; post-load block wrapped, falling back to the always-consistent empty state and reporting the error |
| ARCH-05 | medium | n/a | PrintJob.RunOnStaThread IsBackground=false | A wedged spooler keeps the process alive after all windows close | accepted | Deliberate, documented trade-off: a background thread would silently lose a legitimate slow job when the user closes the app right after Print; a bounded wait needs an owner decision (see residual risks) |
| ARCH-06 | low | low | tools/HangProbe/ContractChecks.cs ZeroPageDocumentRejected | Only InvalidDataException counted as a pass; PDFium refusing the file would have been a false FAIL; temp file never deleted | fixed | Any exception is the contract; fails only if PdfDoc opens the file; temp file deleted in finally |
| ARCH-07 | low | low | PdfDoc rejection message shown inside a localized dialog | English sentence inside the 14-language error dialog | fixed | NoPagesMessage added to all 14 resource files; OpenFileAsync maps UnreadablePagesException to it |
| ARCH-08 | low | low | PrintJob.EnumerateQueues / ResolveQueue | PrintQueueCollection itself not disposed | fixed (partial) | Disposed in EnumerateQueues; left to the finalizer in the rare ResolveQueue fallback because disposing it may dispose the queue being returned (documented in code) |
| ARCH-09 | low | low | MainWindow (no DpiChanged handling) | Off fit-to-view, moving the window to a monitor with another scale leaves bitmaps at the old pixel density until the next scroll/zoom | fixed | OnDpiChanged override re-runs layout, which re-targets the retained window; not encodable in HangProbe (single monitor) |
| P4-2-01 | medium | low | tools/HangProbe/ContractChecks.cs startup-argument check (pass 2) | The check passed vacuously when the probe ran with no arguments (a documented invocation) | fixed | Rule extracted to `App.IsDocumentArgument` and table-tested with 9 cases regardless of argv; the live StartupFile assertion is kept and labelled when no arguments were given |
| P4-2-02 | low | low | tools/HangProbe/ContractChecks.cs rotation check (pass 2) | Synchronous 300 DPI render on the UI thread, unlike every production caller | fixed | Page produced and measured on a dedicated STA thread mirroring PrintJob, dispatcher shut down afterwards |
| FINAL-01 | medium | low | this report (draft) | Draft written before the last code commits said 9 new checks / 51 total; the tree has 10 / 52 | fixed | Report corrected; HangProbe re-run at the final code commit (52 checks, with and without the page-count argument) |
| FINAL-02 | low | low | tools/StoreShots Program.cs colour check; report | 25-74 colours was measured on a scratch run; the shipped fullscreen screenshot samples only 10 at a 32-pixel grid, a margin of 2 over the threshold of 8 | fixed | Grid tightened to 16 pixels and threshold raised to 16: shipped set 59-200, final scratch run 59-200, uniform frame 1, three-level noise 9 |
| FINAL-03 | low | low | tools/HangProbe/ContractChecks.cs print-commit check | `!PrintBtn.IsEnabled` was already true before the job (unshown window never recomputed it), so that clause asserted nothing | fixed | Lock check asserts the enabled-to-disabled transition of Cancel and the printer box; the return check asserts Print becomes enabled after SetPrintingState(false) recomputes it |
| FINAL-04 | low | low | this report (draft) coverage table | File count stated for main (96); the final code commit has 97 | fixed | Manifest restated at the final code commit |

## Fix details and changed files

**Release scripts and docs** (`71729be`): `packaging/Verify-Release.ps1` compares each
architecture's `InstallerSha256` with the local zip it verified earlier; `makeappx unpack`
runs inside the `try/finally`; the one non-ASCII character is gone. `packaging/Build-Msix.ps1`
and `Build-Zip.ps1` validate `-Rid`; the signtool lookup has a not-found guard. `README.md`
gained the "What's new" section `CLAUDE.md` requires.

**Fixtures and ChapterSmoke** (`bdbf3f2`): `tools/MakeChapterFixtures.ps1` generates the xref
table, trailer and `startxref` from the bytes it writes and declares the real stream length;
the six `tools/fixtures/*.pdf` were regenerated and verified offset-for-offset (only
`/Length`, xref offsets, `startxref` and a trailing newline changed; outline semantics did
not). `tools/ChapterSmoke/Program.cs` asserts page count, root count and navigable page list
per fixture.

**StoreShots gate** (`ce71ac2`, tightened in `eaf2c16`): `tools/StoreShots/Program.cs` checks
that the window covers the capture rectangle in device pixels before each capture and rejects
frames sampling fewer than 16 distinct colours on a 16-pixel grid; stale `captions.md` is
deleted with the stale PNGs.

**Viewer, print preview, print job** (`fe4b671`): `PdfDoc` refuses zero-page documents;
`MainWindow.GoToPage` keeps the facing spread on in-spread jumps; `PrintPreviewWindow`
disables Cancel/Escape while a job spools and exposes `ParseRange`, `PrintAsync` and a
`PrintOverride` seam for the probe; `PrintJob` disposes every `PrintQueue`; the unused
paginator constructor is gone; `tools/HangProbe` gained `ContractChecks.cs` and the watchdog
catches the exception `Task.Wait` really throws.

**Architecture repairs** (`0075a1a`): print jobs snapshot `PdfDoc.Rotation` on the calling
thread and carry it through `PdfPrintPaginator` to explicit `GetDisplaySize`/`RenderPageSync`
overloads; `App.OnStartup` accepts only existing files or `.pdf` paths (the first-round rule
had turned the harnesses' positional arguments into a startup file); `PdfDoc` pins
`PageSizes.Count == PageCount` and throws `UnreadablePagesException`, which `OpenFileAsync`
maps to the new localized `NoPagesMessage` (all 14 resx files); the post-load block of
`OpenFileAsync` falls back to the empty state on failure instead of vanishing in a discarded
task; `OnDpiChanged` re-runs layout; `Verify-Release.ps1` fails when no hash line was found;
`PrintJob.EnumerateQueues` disposes the queue collection.

**Harness soundness** (`a56b42b`, `eaf2c16`): the startup-argument rule became the pure
`App.IsDocumentArgument`, table-tested on every run; the rotation check renders on an STA
thread like `PrintJob`; the print-commit check asserts state transitions rather than resting
states; the StoreShots colour sampler uses a 16-pixel grid.

37 files changed on the branch (749 insertions, 228 deletions at `eaf2c16`), plus this
report. No version, manifest identity, package pin, or shipped asset was changed.

## Tests added or changed

- `tools/HangProbe/ContractChecks.cs` (new, 10 checks): zero-page document rejected at open;
  startup argument rule (9 table cases) and non-PDF startup arguments ignored; facing
  in-spread jump keeps the page slots and a cross-spread jump rebuilds them; print controls
  lock while a job spools and return afterwards (via `PrintOverride`); print pages keep the
  rotation the job started with (content box and bitmap checked after a mid-job rotation);
  11 `ParseRange` cases; `PlacePage` scale-to-fit cases. HangProbe: 42 → 52 checks.
- `tools/ChapterSmoke/Program.cs`: per-fixture outline-shape assertions (6 fixtures).
- `tools/StoreShots/Program.cs`: window-coverage and content checks on every capture.
- `tools/HangProbe/UiWatchdog.cs`: shutdown-cancellation catch corrected.

## Validation commands and results

| Command | Baseline (`5058422`) | After pass-1 fixes (`0075a1a`) | Final code commit (`eaf2c16`) |
|---|---|---|---|
| `dotnet build PdfLiteViewer.slnx -c Release` | 0 warnings, 0 errors | 0 warnings, 0 errors | 0 warnings, 0 errors |
| `dotnet run --project tools/ChapterSmoke -c Release -- 'tools/fixtures/*.pdf'` | exit 0, dump only | exit 0, 6/6 shape assertions ok | exit 0, 6/6 |
| `dotnet run --project tools/HangProbe -c Release -- 3000` | `PASS — 42 checks` | `PASS — 51 checks` | `PASS — 52 checks` (also 52 with no arguments at `a56b42b`) |
| `dotnet run --project tools/StoreShots -c Release -- <scratch dir>` | — | `PASS — 9 screenshots` at `ce71ac2` | `PASS — 9 screenshots`; captures sample 59–200 distinct colours on the 16-pixel grid (threshold 16); tracked set untouched |
| `powershell -File packaging/Verify-Release.ps1 -WingetCheck -SkipProvenance` against the real local 1.0.15 MSIX/zips | — | PASS as shipped; FAIL on one flipped hash digit (`71729be`); FAIL with both hash lines removed (`0075a1a`) | unchanged since `0075a1a` |
| PowerShell 5.1 parser over the 4 scripts; byte scan for non-ASCII | — | 0 parse errors; clean | unchanged since `0075a1a` |
| Fixture checker (xref offsets, `startxref`, `/Size`, 20-byte entries, `/Length`) | 6/6 BAD | 6/6 OK | unchanged since `bdbf3f2`; independently reproduced byte-for-byte by the final challenger |
| Throwaway WPF probe (scratch project) | — | PDFium accepts zero-page PDFs (`GetPageCount=0`); `MessageBox.Show` with a closed owner returns without exception; `/Count` too high/low keeps `PageCount`/`PageSizes` consistent | — |

`-SkipProvenance` was required because the artifacts under `packaging/out` were built at
tag `v1.0.15`, not at the review branch head; the gate's provenance and dirty-tree checks
were reviewed, not weakened.

## Convergence history

| Pass | Review | Result |
|---|---|---|
| 1 | 4 Sonnet partitions + Opus architecture (whole `src/`, `HangProbe`, `Verify-Release.ps1`) | 23 partition candidates (2 invalid, 1 accepted, 20 fixed), 9 architecture findings (1 accepted, 8 fixed) — including a regression introduced by the first-round `App.OnStartup` fix and a vacuous branch in the first-round hash check, both repaired before any push |
| 2 | 4 Sonnet partitions over the fixed tree (full coverage of all 71 files, not diff-only) | P1, P2, P3: every prior finding confirmed resolved, no new findings. P4: 2 low harness-soundness findings, fixed in `a56b42b` |
| Final | Independent Opus challenge of the result (all hunks, whole `src/`, harness order, gate, policy) | No code defect; 4 report-accuracy / test-strength findings (`FINAL-01..04`), fixed in `eaf2c16` and in this report; 11 hazards chased and refuted with the guard that rules each out |

The pass-1 Opus architecture review failed four times with HTTP 529 (server overload) before
succeeding on the fifth attempt; one interim attempt with a higher-tier model also failed with
529. No review step was simulated by the controller. Five passes were budgeted; convergence
was reached after pass 2 plus the final challenge.

## Commits and rollback

| Commit | Subject |
|---|---|
| `71729be` | Harden the release scripts and add the README "What's new" section |
| `bdbf3f2` | Make the chapter fixtures well-formed and have ChapterSmoke assert their shape |
| `ce71ac2` | Give the StoreShots screenshot gate a way to fail |
| `fe4b671` | Refuse zero-page PDFs, keep the facing spread on in-spread jumps, commit print jobs |
| `0075a1a` | Snapshot print rotation, narrow the startup-file rule, harden open and the hash gate |
| `a56b42b` | Make the probe's startup-argument and print-rotation checks sound |
| `eaf2c16` | Tighten the StoreShots content check and the probe's print-commit assertions |
| (this commit) | Add the review report |

Rollback: the branch is additive on top of `5058422`; reverting the pull request (or
`git revert` of the listed commits in reverse order) restores `main` exactly. The regenerated
fixtures and the resx additions carry no data migration; nothing outside the repository was
changed.

## Remote branch and merge proposal

- Branch: <https://github.com/greenyogainc/pdf-lite-viewer/tree/code-review/full-codebase-review-20260903-0809>
  (pushed without force; remote head `eaf2c16` at the time of the report, then this commit).
- Pull request: <https://github.com/greenyogainc/pdf-lite-viewer/pull/3> targeting `main`.
  The merge is proposed, not performed; auto-merge is not enabled.

## Pipeline

The repository has **no CI provider configured**: `gh api repos/greenyogainc/pdf-lite-viewer/actions/workflows`
returns `total_count: 0`; no `.github/` directory or other CI definition is tracked; `main`
has no branch protection or rulesets. For the pushed code commit `eaf2c16`, the commit API
reports 0 check runs, 0 check suites and 0 statuses. Per the review protocol this is
`NO PIPELINE CONFIGURED`, not a pass.

This report cannot record a pipeline result for the SHA that contains it; the authoritative
evidence for the final SHA is in the pull request description and the final response.

## Blockers and residual risks

- **No CI.** The only automated evidence is local. Recommended next action: add a GitHub
  Actions workflow on `windows-latest` running the documented gates (`dotnet build -c Release`
  with warnings as errors, `ChapterSmoke`); `HangProbe` and `StoreShots` need an interactive
  desktop and remain local gates. This was deliberately left out of the review branch as a
  scope decision for the owner.
- **Print thread lifetime (ARCH-05, accepted).** `PrintJob` runs jobs on a foreground thread
  so that closing the app right after Print does not lose the job. A wedged spooler therefore
  keeps `PdfLiteViewer.exe` alive with no window until the spooler releases it. A bounded
  wait would trade that for silently lost slow jobs; the owner should decide.
- **Printer-less machines (P4-03, accepted).** HangProbe's printer-list check reports a
  labelled skip when no printer is installed; it cannot be exercised there.
- **`ResolveQueue` fallback (ARCH-08, partial).** The `PrintQueueCollection` is left to the
  finalizer on the rare fallback path because disposing it may dispose the queue being
  returned.
- **`OnDpiChanged` and the negative side of the StoreShots content check** could not be
  exercised by the harnesses (single monitor; the blank-frame case was measured on synthetic
  data — 1 colour uniform, about 9 for three-level noise — not on a real failed capture).
- Version was not bumped and no release artifacts were built: this branch changes no shipped
  behaviour that warrants a release on its own, and `CLAUDE.md` requires the artifact check
  before any bump. When the next release is cut, its "What's new" entry belongs in the new
  README section.

## Recommendation

Merge after a human read of pull request #3. The implementation was reviewed twice by
independent agents plus an architecture review and a final challenge that found no remaining
code defect, and every repository-native gate passes at the final code commit. The status is
INCOMPLETE only because the repository has no pipeline to confirm the final SHA; adding CI is
the recommended first follow-up, and the print-thread lifetime trade-off (ARCH-05) is the one
open product decision.
