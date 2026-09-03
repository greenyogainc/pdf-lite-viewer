# Creates minimal PDF fixtures exercising outline shapes for GetChapters validation.
# Pure PDF syntax - no extra dependencies.
$ErrorActionPreference = "Stop"
$outDir = Join-Path $PSScriptRoot "fixtures"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

# Writes a PDF from its body objects (header through the last endobj) and generates the
# cross-reference table, trailer and startxref from the bytes actually written. The
# tables used to be typed by hand and every offset in them was wrong; PDFium and PdfPig
# only opened the fixtures by rebuilding the xref, so tools/ChapterSmoke was exercising
# their recovery paths rather than a well-formed file.
function Write-Pdf($path, $body) {
    $body = ($body -replace "`r`n", "`n").TrimEnd("`n") + "`n"
    $bytes = [System.Text.Encoding]::ASCII.GetBytes($body)
    if ($bytes.Length -ne $body.Length) { throw "$path : body must be pure ASCII (char index == byte offset)" }

    # Every "N 0 obj" at the start of a line; its char index is its byte offset.
    $offsets = @{}
    foreach ($m in [regex]::Matches($body, '(?m)^(\d+) 0 obj')) {
        $offsets[[int]$m.Groups[1].Value] = $m.Index
    }
    if ($offsets.Count -eq 0) { throw "$path : no objects found" }
    $size = ($offsets.Keys | Measure-Object -Maximum).Maximum + 1

    # Each xref entry is exactly 20 bytes: 10-digit offset, space, 5-digit generation,
    # space, n/f, space, LF. Object numbers the body skips become free entries.
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.Append("xref`n0 $size`n")
    [void]$sb.Append("0000000000 65535 f `n")
    for ($i = 1; $i -lt $size; $i++) {
        if ($offsets.ContainsKey($i)) { [void]$sb.Append(("{0:D10} 00000 n `n" -f $offsets[$i])) }
        else                          { [void]$sb.Append("0000000000 65535 f `n") }
    }
    [void]$sb.Append("trailer<< /Size $size /Root 1 0 R >>`nstartxref`n$($bytes.Length)`n%%EOF`n")
    $tail = [System.Text.Encoding]::ASCII.GetBytes($sb.ToString())

    $out = New-Object System.IO.MemoryStream
    $out.Write($bytes, 0, $bytes.Length)
    $out.Write($tail, 0, $tail.Length)
    [System.IO.File]::WriteAllBytes($path, $out.ToArray())
    Write-Host "Wrote $path ($($out.Length) bytes, $($offsets.Count) objects)"
}

# --- nested-internal.pdf: nested Document destinations ---
Write-Pdf (Join-Path $outDir "nested-internal.pdf") @'
%PDF-1.4
1 0 obj<< /Type /Catalog /Pages 2 0 R /Outlines 8 0 R /PageMode /UseOutlines >>endobj
2 0 obj<< /Type /Pages /Kids [3 0 R 4 0 R 5 0 R] /Count 3 >>endobj
3 0 obj<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 6 0 R /Resources << /Font << /F1 7 0 R >> >> >>endobj
4 0 obj<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 6 0 R /Resources << /Font << /F1 7 0 R >> >> >>endobj
5 0 obj<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 6 0 R /Resources << /Font << /F1 7 0 R >> >> >>endobj
6 0 obj<< /Length 38 >>stream
BT /F1 12 Tf 72 720 Td (Fixture) Tj ET
endstream
endobj
7 0 obj<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>endobj
8 0 obj<< /Type /Outlines /First 9 0 R /Last 9 0 R /Count 3 >>endobj
9 0 obj<< /Title (Part I) /Parent 8 0 R /First 10 0 R /Last 11 0 R /Count 2 /Dest [3 0 R /Fit] >>endobj
10 0 obj<< /Title (Chapter A) /Parent 9 0 R /Next 11 0 R /Dest [4 0 R /Fit] >>endobj
11 0 obj<< /Title (Chapter B) /Parent 9 0 R /Prev 10 0 R /Dest [5 0 R /Fit] >>endobj
'@

# --- container-only.pdf: outline nodes without Dest (containers) ---
Write-Pdf (Join-Path $outDir "container-only.pdf") @'
%PDF-1.4
1 0 obj<< /Type /Catalog /Pages 2 0 R /Outlines 6 0 R >>endobj
2 0 obj<< /Type /Pages /Kids [3 0 R] /Count 1 >>endobj
3 0 obj<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>endobj
4 0 obj<< /Length 38 >>stream
BT /F1 12 Tf 72 720 Td (Fixture) Tj ET
endstream
endobj
5 0 obj<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>endobj
6 0 obj<< /Type /Outlines /First 7 0 R /Last 7 0 R /Count 2 >>endobj
7 0 obj<< /Title (Section) /Parent 6 0 R /First 8 0 R /Last 8 0 R /Count 1 >>endobj
8 0 obj<< /Title (Subsection) /Parent 7 0 R >>endobj
'@

# --- no-outline.pdf ---
Write-Pdf (Join-Path $outDir "no-outline.pdf") @'
%PDF-1.4
1 0 obj<< /Type /Catalog /Pages 2 0 R >>endobj
2 0 obj<< /Type /Pages /Kids [3 0 R] /Count 1 >>endobj
3 0 obj<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>endobj
4 0 obj<< /Length 38 >>stream
BT /F1 12 Tf 72 720 Td (Fixture) Tj ET
endstream
endobj
5 0 obj<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>endobj
'@

# --- uri-external.pdf: URI action + GoToR-style external (via A action URI) ---
Write-Pdf (Join-Path $outDir "uri-external.pdf") @'
%PDF-1.4
1 0 obj<< /Type /Catalog /Pages 2 0 R /Outlines 6 0 R >>endobj
2 0 obj<< /Type /Pages /Kids [3 0 R] /Count 1 >>endobj
3 0 obj<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>endobj
4 0 obj<< /Length 38 >>stream
BT /F1 12 Tf 72 720 Td (Fixture) Tj ET
endstream
endobj
5 0 obj<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>endobj
6 0 obj<< /Type /Outlines /First 7 0 R /Last 8 0 R /Count 2 >>endobj
7 0 obj<< /Title (Internal) /Parent 6 0 R /Next 8 0 R /Dest [3 0 R /Fit] >>endobj
8 0 obj<< /Title (Website) /Parent 6 0 R /Prev 7 0 R /A << /S /URI /URI (https://example.com) >> >>endobj
'@

# --- malformed-outline.pdf: dangling /Outlines reference (should degrade to no bookmarks) ---
Write-Pdf (Join-Path $outDir "malformed-outline.pdf") @'
%PDF-1.4
1 0 obj<< /Type /Catalog /Pages 2 0 R /Outlines 99 0 R >>endobj
2 0 obj<< /Type /Pages /Kids [3 0 R] /Count 1 >>endobj
3 0 obj<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>endobj
4 0 obj<< /Length 38 >>stream
BT /F1 12 Tf 72 720 Td (Fixture) Tj ET
endstream
endobj
5 0 obj<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>endobj
'@

# --- named-destinations.pdf: outline entry resolved via the Names/Dests tree ---
# (Object 6 is deliberately unused: the generator must emit a free xref entry for it.)
Write-Pdf (Join-Path $outDir "named-destinations.pdf") @'
%PDF-1.4
1 0 obj<< /Type /Catalog /Pages 2 0 R /Outlines 8 0 R /Names 9 0 R /PageMode /UseOutlines >>endobj
2 0 obj<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>endobj
3 0 obj<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 5 0 R /Resources << /Font << /F1 7 0 R >> >> >>endobj
4 0 obj<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 5 0 R /Resources << /Font << /F1 7 0 R >> >> >>endobj
5 0 obj<< /Length 38 >>stream
BT /F1 12 Tf 72 720 Td (Fixture) Tj ET
endstream
endobj
7 0 obj<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>endobj
8 0 obj<< /Type /Outlines /First 10 0 R /Last 10 0 R /Count 1 >>endobj
9 0 obj<< /Dests 11 0 R >>endobj
10 0 obj<< /Title (Named Jump) /Parent 8 0 R /Dest (chap1) >>endobj
11 0 obj<< /Names [(chap1) 12 0 R] >>endobj
12 0 obj[4 0 R /Fit]endobj
'@

Write-Host "Fixtures ready in $outDir"
