# Measures line and branch coverage of PdfLiteViewer.dll across the test harnesses,
# merging every run into one report. XAML-generated code is not counted.
#
#   tools\Measure-Coverage.ps1 [-Pages 600] [-TimeoutMinutes 20] [-MinLine 0] [-MinBranch 0]
#
# Needs an interactive desktop: HangProbe really shows windows and takes the foreground, so
# do not type while it runs. Results land in coverage\ (coverage.cobertura.xml, gaps.txt,
# and one log per harness run).
param(
    # Enough chapters to recycle sidebar containers; at 40 the echo-guard check cannot run.
    [int]$Pages = 600,
    [int]$TimeoutMinutes = 20,
    [double]$MinLine = 0,
    [double]$MinBranch = 0,
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root "coverage"
Set-Location $root

function Get-HarnessDir($project) {
    $dir = Get-ChildItem (Join-Path $root "tools\$project\bin\Debug") -Directory |
        Where-Object { Test-Path (Join-Path $_.FullName "$project.dll") } | Select-Object -First 1
    if (-not $dir) { throw "No Debug build of $project. Run without -SkipBuild." }
    return $dir.FullName
}

# Start-Process joins its arguments with spaces, so every value is quoted here.
function Quote($value) { return '"' + $value + '"' }

function Invoke-Harness($label, $project, $harnessArgs, [switch]$Last) {
    $dir = Get-HarnessDir $project
    $merged = Join-Path $out "coverage.json"
    $coverletArgs = @(
        "tool", "run", "coverlet",
        (Quote (Join-Path $dir "$project.dll")),
        "--target", (Quote (Join-Path $dir "$project.exe")),
        "--targetargs", (Quote $harnessArgs),
        "--include", (Quote "[PdfLiteViewer]*"),
        "--exclude-by-file", (Quote "**/obj/**"),
        "--exclude-by-attribute", "GeneratedCode",
        "--output", (Quote (Join-Path $out "coverage")),
        "--format", "json"
    )
    if (Test-Path $merged) { $coverletArgs += @("--merge-with", (Quote $merged)) }
    if ($Last) { $coverletArgs += @("--format", "cobertura") }

    $log = Join-Path $out "$label.log"
    $err = Join-Path $out "$label.err.log"
    Write-Host "--- $label : $project $harnessArgs"
    $process = Start-Process dotnet -ArgumentList $coverletArgs -NoNewWindow -PassThru `
        -RedirectStandardOutput $log -RedirectStandardError $err
    # Reading the handle makes the Process object cache it; without that, ExitCode comes
    # back empty after WaitForExit(timeout) and every harness looks like it failed.
    $null = $process.Handle
    # A modal dialog nothing dismisses would otherwise block the run forever, and coverlet
    # only writes its hits when the target exits.
    if (-not $process.WaitForExit($TimeoutMinutes * 60 * 1000)) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        Get-Process $project -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
        throw "$label did not finish within $TimeoutMinutes minutes; see $log"
    }
    $code = $process.ExitCode
    if ($null -eq $code) { throw "$label finished but its exit code could not be read; see $log" }
    Get-Content $log | Where-Object { $_ -match '^(PASS|FAIL) |^FAIL|probe failed' } | ForEach-Object { Write-Host "    $_" }
    Write-Host "    exit code $code"
    return $code
}

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Path $out | Out-Null

& dotnet tool restore | Out-Null
if (-not $SkipBuild) {
    foreach ($project in "ChapterSmoke", "HangProbe") {
        & dotnet build "tools\$project" -c Debug -nologo -v q | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Debug build of $project failed." }
    }
}

$fixtures = Join-Path $root "tools\fixtures\*.pdf"
$startupPdf = Join-Path $root "tools\fixtures\no-outline.pdf"
# The inner backslash-quotes survive Quote and reach the harness as a quoted path.
$startupArgs = '{0} --startup-check \"{1}\" --coverage' -f $Pages, $startupPdf
$failures = @()
$runs = @(
    @{ Label = "chaptersmoke"; Project = "ChapterSmoke"; Args = $fixtures },
    @{ Label = "about-check"; Project = "HangProbe"; Args = "$Pages --about-check --coverage" },
    @{ Label = "startup-check"; Project = "HangProbe"; Args = $startupArgs },
    # The only run that reaches the real spooler call, and only for Microsoft Print to PDF.
    @{ Label = "real-print"; Project = "HangProbe"; Args = "$Pages --real-print-to-pdf --coverage" },
    @{ Label = "probe"; Project = "HangProbe"; Args = "$Pages --coverage" }
)
for ($i = 0; $i -lt $runs.Count; $i++) {
    $run = $runs[$i]
    $code = Invoke-Harness $run.Label $run.Project $run.Args -Last:($i -eq $runs.Count - 1)
    if ($code -ne 0) { $failures += "$($run.Label) exited $code" }
}

$jsonPath = Join-Path $out "coverage.json"
if (-not (Test-Path $jsonPath)) { throw "Coverlet wrote no report; see the logs in $out" }
$coverage = Get-Content $jsonPath -Raw | ConvertFrom-Json

# Rounded down, so a small gap can never read as 100%.
function Get-Percent($hit, $all) {
    if ($all -eq 0) { return 100 }
    return [Math]::Floor(10000.0 * $hit / $all) / 100
}

# From coverlet's JSON: the Cobertura report's per-line figure undercounts a large switch.
# One source line can appear in several generated classes (a lambda's closure, an async
# state machine), so a line counts as covered when any of them ran it.
$byFile = @{}
foreach ($module in $coverage.PSObject.Properties) {
    foreach ($document in $module.Value.PSObject.Properties) {
        $entry = @{ Lines = @{}; Branches = @{} }
        $byFile[$document.Name] = $entry
        foreach ($class in $document.Value.PSObject.Properties) {
            foreach ($method in $class.Value.PSObject.Properties) {
                foreach ($line in $method.Value.Lines.PSObject.Properties) {
                    $number = [int]$line.Name
                    $known = if ($entry.Lines.ContainsKey($number)) { $entry.Lines[$number] } else { 0 }
                    $entry.Lines[$number] = [Math]::Max([long]$known, [long]$line.Value)
                }
                foreach ($branch in $method.Value.Branches) {
                    $number = [int]$branch.Line
                    $seen = if ($entry.Branches.ContainsKey($number)) { $entry.Branches[$number] } else { @(0, 0) }
                    $entry.Branches[$number] = @(($seen[0] + [int]($branch.Hits -gt 0)), ($seen[1] + 1))
                }
            }
        }
    }
}

$linesCovered = 0; $linesTotal = 0; $branchesCovered = 0; $branchesTotal = 0
$gaps = New-Object System.Collections.Generic.List[string]
$rows = foreach ($file in ($byFile.Keys | Sort-Object)) {
    $lines = $byFile[$file].Lines
    $branches = $byFile[$file].Branches
    $missedLines = @($lines.Keys | Where-Object { $lines[$_] -eq 0 } | Sort-Object)
    $partial = @($branches.Keys | Where-Object { $branches[$_][0] -lt $branches[$_][1] } | Sort-Object)
    $branchHit = ($branches.Values | ForEach-Object { $_[0] } | Measure-Object -Sum).Sum
    $branchAll = ($branches.Values | ForEach-Object { $_[1] } | Measure-Object -Sum).Sum
    $linesCovered += $lines.Count - $missedLines.Count
    $linesTotal += $lines.Count
    $branchesCovered += [int]$branchHit
    $branchesTotal += [int]$branchAll
    $name = Split-Path $file -Leaf
    if ($missedLines.Count -gt 0) { $gaps.Add("$name uncovered lines: " + ($missedLines -join ", ")) }
    if ($partial.Count -gt 0) {
        $detail = $partial | ForEach-Object { "$_ ($($branches[$_][0])/$($branches[$_][1]))" }
        $gaps.Add("$name partial branches: " + ($detail -join ", "))
    }
    [pscustomobject]@{
        File = $name
        Lines = "$($lines.Count - $missedLines.Count)/$($lines.Count)"
        Branches = "$([int]$branchHit)/$([int]$branchAll)"
    }
}

$linePercent = Get-Percent $linesCovered $linesTotal
$branchPercent = Get-Percent $branchesCovered $branchesTotal
$rows | Format-Table -AutoSize | Out-String | Write-Host
Write-Host "TOTAL line $linePercent% ($linesCovered/$linesTotal), branch $branchPercent% ($branchesCovered/$branchesTotal)"
[IO.File]::WriteAllLines((Join-Path $out "gaps.txt"), [string[]]$gaps)
if ($gaps.Count -eq 0) { Write-Host "No uncovered lines or partial branches." }
else { Write-Host "Uncovered lines and partial branches: $(Join-Path $out 'gaps.txt')" }

if ($failures.Count -gt 0) { Write-Host ("Harness failures: " + ($failures -join "; ")); exit 1 }
if ($linePercent -lt $MinLine) { Write-Host "Line coverage is below $MinLine%."; exit 1 }
if ($branchPercent -lt $MinBranch) { Write-Host "Branch coverage is below $MinBranch%."; exit 1 }
