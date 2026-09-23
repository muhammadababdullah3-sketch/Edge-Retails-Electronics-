param([string]$Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path)
$ErrorActionPreference = 'Stop'
$canonical = Join-Path $Root 'docs\Edge_Retails_Final_Architecture_Report_v1.md'
$pointer = Join-Path $Root 'EDGE_RETAILS_BACKEND_ARCHITECTURE_FINAL.md'
$manifest = Join-Path $Root 'docs\Architecture_Authority_Manifest.json'
$fail = [System.Collections.Generic.List[string]]::new()
if(-not (Test-Path $canonical)){ throw "Missing canonical architecture: $canonical" }
$text = [IO.File]::ReadAllText($canonical)
$lines = Get-Content $canonical
$headingNumbers = @()
foreach($line in $lines){ if($line -match '^#\s+(\d+)\.'){ $headingNumbers += [int]$Matches[1] } }
if($headingNumbers.Count -ne 234){ $fail.Add("Expected 234 numbered sections (0..233), got $($headingNumbers.Count)") }
$dup = $headingNumbers | Group-Object | Where-Object Count -gt 1
if($dup){ $fail.Add('Duplicate numbered sections: ' + (($dup | ForEach-Object Name) -join ',')) }
$missing = 0..233 | Where-Object { $_ -notin $headingNumbers }
if($missing){ $fail.Add('Missing numbered sections: ' + ($missing -join ',')) }
$fenceCount = ([regex]::Matches($text,'```')).Count
if(($fenceCount % 2) -ne 0){ $fail.Add("Unbalanced Markdown fences: $fenceCount") }
$mustContain = @(
'## 233.1 International Forensic Audit Remediation Annex - 2026-09-22',
'CanonicalSha256',
'authoritative shop currency is `PKR`',
'AttributesSchemaVersion',
'REMOTE_MANIFEST_AUTH_VERIFIED',
'INTENT_PERSISTED',
'Audit Retention, Redaction, and Growth Governance',
'statement/command timeout',
'Application / Database Compatibility Matrix',
'Terminal Registration, Session Freshness, and Protocol Authority',
'Scanner Namespace, Normalization, and Historical Barcode Identity',
'Exact-Unit State to Accounting Mapping',
'Idempotency Retention, Print Provenance, and Purge Safety',
'Physical Identity Encoding, TrackingCode Label, Serial, and IMEI Normalization',
'Stable Error Contract and Localization Boundary'
)
foreach($m in $mustContain){ if(-not $text.Contains($m)){ $fail.Add("Missing remediation contract: $m") } }
if($text.Contains('See Section 237.')){ $fail.Add('Broken Section 237 cross-reference remains') }
if($text -match '\bWithServiceCenter\b'){ $fail.Add('Stale WithServiceCenter custody remains') }
if(-not $text.Contains('ItemSequence 1000000 remains valid')){ $fail.Add('No explicit >999999 regression guard') }
if(-not $text.Contains('exactly 19 full screens')){ $fail.Add('19-screen authority missing') }
if(-not $text.Contains('Supplier Accounts Payable is no longer deferred')){ $fail.Add('Supplier Khata promotion missing') }
if(-not $text.Contains('RecoveryDifference')){ $fail.Add('Warranty credit recovery-difference rule missing') }
if(-not $text.Contains('WARRANTY_CUSTOMER_HELD')){ $fail.Add('Customer warranty non-stock state missing') }
if(-not $text.Contains('FIRST_PRODUCTION_BASELINE_FREEZE')){ $fail.Add('Migration baseline freeze rule missing') }
if(-not (Test-Path $pointer)){ $fail.Add('Root canonical pointer missing') } else {
  $pointerText=[IO.File]::ReadAllText($pointer)
  if($pointerText.Length -gt 2500){ $fail.Add('Root pointer is too large; architecture body may have been duplicated') }
  if(-not $pointerText.Contains('no duplicate architecture body')){ $fail.Add('Root pointer-only contract missing') }
}
if(Test-Path $manifest){
  $m = Get-Content -Raw $manifest | ConvertFrom-Json
  $actual=(Get-FileHash $canonical -Algorithm SHA256).Hash
  if($m.CanonicalSha256 -ne $actual){ $fail.Add("Manifest SHA mismatch: expected $($m.CanonicalSha256), actual $actual") }
}
if($fail.Count -gt 0){
  Write-Host 'ARCHITECTURE INTERNATIONAL AUDIT REMEDIATION: FAIL' -ForegroundColor Red
  $fail | ForEach-Object { Write-Host " - $_" -ForegroundColor Red }
  exit 1
}
Write-Host 'ARCHITECTURE INTERNATIONAL AUDIT REMEDIATION: PASS' -ForegroundColor Green
Write-Host "NumberedSections=$($headingNumbers.Count); Fences=$fenceCount; CanonicalSHA=$((Get-FileHash $canonical -Algorithm SHA256).Hash)"
exit 0