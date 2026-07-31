# Creates minimal PDF fixtures exercising outline shapes for GetChapters validation.
# Pure PDF syntax — no extra dependencies.
$ErrorActionPreference = "Stop"
$outDir = Join-Path $PSScriptRoot "fixtures"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

function Write-Pdf($path, $body) {
    # Normalize to LF and ensure final newline for stable offsets if we ever need xref rebuilds.
    $bytes = [System.Text.Encoding]::ASCII.GetBytes(($body -replace "`r`n", "`n"))
    [System.IO.File]::WriteAllBytes($path, $bytes)
    Write-Host "Wrote $path ($($bytes.Length) bytes)"
}

# Helper: one blank page content stream
$pageContent = "BT /F1 12 Tf 100 700 Td (Page) Tj ET"

# --- nested-internal.pdf: nested Document destinations ---
Write-Pdf (Join-Path $outDir "nested-internal.pdf") @'
%PDF-1.4
1 0 obj<< /Type /Catalog /Pages 2 0 R /Outlines 8 0 R /PageMode /UseOutlines >>endobj
2 0 obj<< /Type /Pages /Kids [3 0 R 4 0 R 5 0 R] /Count 3 >>endobj
3 0 obj<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 6 0 R /Resources << /Font << /F1 7 0 R >> >> >>endobj
4 0 obj<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 6 0 R /Resources << /Font << /F1 7 0 R >> >> >>endobj
5 0 obj<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 6 0 R /Resources << /Font << /F1 7 0 R >> >> >>endobj
6 0 obj<< /Length 44 >>stream
BT /F1 12 Tf 72 720 Td (Fixture) Tj ET
endstream
endobj
7 0 obj<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>endobj
8 0 obj<< /Type /Outlines /First 9 0 R /Last 9 0 R /Count 3 >>endobj
9 0 obj<< /Title (Part I) /Parent 8 0 R /First 10 0 R /Last 11 0 R /Count 2 /Dest [3 0 R /Fit] >>endobj
10 0 obj<< /Title (Chapter A) /Parent 9 0 R /Next 11 0 R /Dest [4 0 R /Fit] >>endobj
11 0 obj<< /Title (Chapter B) /Parent 9 0 R /Prev 10 0 R /Dest [5 0 R /Fit] >>endobj
xref
0 12
0000000000 65535 f 
0000000009 00000 n 
0000000096 00000 n 
0000000165 00000 n 
0000000294 00000 n 
0000000423 00000 n 
0000000552 00000 n 
0000000645 00000 n 
0000000714 00000 n 
0000000787 00000 n 
0000000892 00000 n 
0000000968 00000 n 
trailer<< /Size 12 /Root 1 0 R >>
startxref
1044
%%EOF
'@

# --- container-only.pdf: outline nodes without Dest (containers) ---
Write-Pdf (Join-Path $outDir "container-only.pdf") @'
%PDF-1.4
1 0 obj<< /Type /Catalog /Pages 2 0 R /Outlines 6 0 R >>endobj
2 0 obj<< /Type /Pages /Kids [3 0 R] /Count 1 >>endobj
3 0 obj<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>endobj
4 0 obj<< /Length 44 >>stream
BT /F1 12 Tf 72 720 Td (Fixture) Tj ET
endstream
endobj
5 0 obj<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>endobj
6 0 obj<< /Type /Outlines /First 7 0 R /Last 7 0 R /Count 2 >>endobj
7 0 obj<< /Title (Section) /Parent 6 0 R /First 8 0 R /Last 8 0 R /Count 1 >>endobj
8 0 obj<< /Title (Subsection) /Parent 7 0 R >>endobj
xref
0 9
0000000000 65535 f 
0000000009 00000 n 
0000000074 00000 n 
0000000129 00000 n 
0000000258 00000 n 
0000000351 00000 n 
0000000420 00000 n 
0000000487 00000 n 
0000000575 00000 n 
trailer<< /Size 9 /Root 1 0 R >>
startxref
0633
%%EOF
'@

# --- no-outline.pdf ---
Write-Pdf (Join-Path $outDir "no-outline.pdf") @'
%PDF-1.4
1 0 obj<< /Type /Catalog /Pages 2 0 R >>endobj
2 0 obj<< /Type /Pages /Kids [3 0 R] /Count 1 >>endobj
3 0 obj<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>endobj
4 0 obj<< /Length 44 >>stream
BT /F1 12 Tf 72 720 Td (Fixture) Tj ET
endstream
endobj
5 0 obj<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>endobj
xref
0 6
0000000000 65535 f 
0000000009 00000 n 
0000000058 00000 n 
0000000113 00000 n 
0000000242 00000 n 
0000000335 00000 n 
trailer<< /Size 6 /Root 1 0 R >>
startxref
0404
%%EOF
'@

# --- uri-external.pdf: URI action + GoToR-style external (via A action URI) ---
Write-Pdf (Join-Path $outDir "uri-external.pdf") @'
%PDF-1.4
1 0 obj<< /Type /Catalog /Pages 2 0 R /Outlines 6 0 R >>endobj
2 0 obj<< /Type /Pages /Kids [3 0 R] /Count 1 >>endobj
3 0 obj<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>endobj
4 0 obj<< /Length 44 >>stream
BT /F1 12 Tf 72 720 Td (Fixture) Tj ET
endstream
endobj
5 0 obj<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>endobj
6 0 obj<< /Type /Outlines /First 7 0 R /Last 8 0 R /Count 2 >>endobj
7 0 obj<< /Title (Internal) /Parent 6 0 R /Next 8 0 R /Dest [3 0 R /Fit] >>endobj
8 0 obj<< /Title (Website) /Parent 6 0 R /Prev 7 0 R /A << /S /URI /URI (https://example.com) >> >>endobj
xref
0 9
0000000000 65535 f 
0000000009 00000 n 
0000000074 00000 n 
0000000129 00000 n 
0000000258 00000 n 
0000000351 00000 n 
0000000420 00000 n 
0000000487 00000 n 
0000000573 00000 n 
trailer<< /Size 9 /Root 1 0 R >>
startxref
0675
%%EOF
'@

Write-Host "Fixtures ready in $outDir"
