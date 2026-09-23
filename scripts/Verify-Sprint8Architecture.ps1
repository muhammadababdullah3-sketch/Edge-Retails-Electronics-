param([string]$Root = ".")
$ErrorActionPreference = "Stop"
$failures = @()

$desktop = Join-Path $Root "src/EdgeRetails.Desktop"
if (Test-Path $desktop) {
    $bad = Get-ChildItem $desktop -Recurse -File -Filter *.cs | Select-String -Pattern "DbContext|NpgsqlConnection|NpgsqlCommand|SaveChanges\(" -CaseSensitive:$false
    if ($bad) { $paths = ($bad.Path | Sort-Object -Unique) -join ", "
    $failures += "Desktop contains direct persistence/database API usage: $paths" }
}

$appStartup = Join-Path $desktop "App.xaml.cs"
$backendRuntime = Join-Path $desktop "Services/BackendRuntime.cs"
if (-not (Test-Path $appStartup) -or -not (Test-Path $backendRuntime)) {
    $failures += "Desktop production startup artifacts are missing."
}
else {
    $appText = Get-Content $appStartup -Raw
    $runtimeText = Get-Content $backendRuntime -Raw
    if ($appText -match 'TryCreateFromEnvironment') {
        $failures += "Desktop can still silently fall back when production backend configuration is missing."
    }
    if ($appText -notmatch 'BackendRuntime\.CreateFromEnvironment') {
        $failures += "Desktop does not require production backend creation outside explicit Debug preview mode."
    }
    if ($runtimeText -notmatch 'ProductionMaintenanceState' -or
        $runtimeText -notmatch 'IProductionMaintenanceBarrier') {
        $failures += "Desktop backend startup does not enforce the production maintenance/recovery gate."
    }
    if ($runtimeText -notmatch 'cannot fall back to demo data') {
        $failures += "Missing backend configuration is not explicitly fail-closed."
    }
}

$self = $MyInvocation.MyCommand.Path
$allTextFiles = Get-ChildItem $Root -Recurse -File | Where-Object { ($_.Extension -in @('.cs','.json','.xml','.config','.ps1','.wxs','.md')) -and $_.FullName -ne $self }
$secretPatterns = @(
    'Password\s*=\s*"[^\"]+"',
    'Host=[^"'']*Password=(?!\$)[^;"''\s]+',
    'PGPASSWORD\s*=\s*["''][^$]'
)
foreach ($pattern in $secretPatterns) {
    $matches = $allTextFiles | Select-String -Pattern $pattern -CaseSensitive:$false
    if ($matches) {
        $paths = ($matches.Path | Sort-Object -Unique) -join ", "
        $failures += "Possible persisted secret matched pattern '$pattern' in: $paths"
    }
}

$screenCandidates = Get-ChildItem $desktop -Recurse -Filter *.xaml -ErrorAction SilentlyContinue | Where-Object { $_.Name -match 'View\.xaml$' }
if ($screenCandidates.Count -gt 0) {
    $screen18 = $screenCandidates | Where-Object { $_.BaseName -match 'Screen18|Production|Deployment' }
    if ($screen18) { $failures += "Potential unnecessary Sprint 8 full screen detected." }
}

if ($failures.Count) {
    $failures | ForEach-Object { Write-Error $_ }
    exit 1
}
Write-Host "Sprint 8 static architecture audit PASS"
