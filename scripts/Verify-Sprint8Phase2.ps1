param([string]$Root = ".")
$ErrorActionPreference = "Stop"
$failures = @()

function Require-Pattern([string]$Path, [string]$Pattern, [string]$Message) {
    $full = Join-Path $Root $Path
    if (-not (Test-Path $full)) { $script:failures += "Missing file: $Path"; return }
    if (-not (Select-String -Path $full -Pattern $Pattern -Quiet)) { $script:failures += $Message }
}
function Reject-Pattern([string]$Path, [string]$Pattern, [string]$Message) {
    $full = Join-Path $Root $Path
    if ((Test-Path $full) -and (Select-String -Path $full -Pattern $Pattern -Quiet)) { $script:failures += $Message }
}

Require-Pattern "src/EdgeRetails.Application/Production/Printing/PrintingHandlers.cs" "EnsureCanPrintAsync" "Print handler does not enforce document authorization before printing."
Require-Pattern "src/EdgeRetails.Application/Production/Licensing/LicenseHandlers.cs" "ExistsAsync" "Initial license import is not guarded by installed-license state."
Require-Pattern "src/EdgeRetails.Application/Production/Licensing/LicenseHandlers.cs" "SettingsManage" "Authorized license replacement does not enforce settings.manage."
Require-Pattern "src/EdgeRetails.Infrastructure/Production/Startup/EfMigrationCompatibilityProbe.cs" "GetAppliedMigrationsAsync" "Migration probe does not inspect applied DB migrations."
Require-Pattern "src/EdgeRetails.Infrastructure/Production/Startup/EfMigrationCompatibilityProbe.cs" "GetMigrations\(\)" "Migration probe does not inspect application migrations."
Require-Pattern "src/EdgeRetails.Infrastructure/Production/Startup/EfMigrationCompatibilityProbe.cs" "DatabaseAhead" "Migration probe cannot fail closed when DB is ahead of application."
Require-Pattern "src/EdgeRetails.Infrastructure/Production/Startup/EfMigrationCompatibilityProbe.cs" "HasPendingModelChanges" "Runtime migration probe does not detect model/snapshot drift."
Require-Pattern "src/EdgeRetails.Application/Production/Startup/ProductionStartupCoordinator.cs" "RecommendedAction" "Startup failures are not actionable."
Reject-Pattern "src/EdgeRetails.Infrastructure/Production/Startup/NpgsqlDatabaseReadinessProbe.cs" "ex\.Message" "Database readiness exposes raw exception messages."
Require-Pattern "src/EdgeRetails.Infrastructure/Production/Backup/AesGcmBackupProtector.cs" "ERBAK002" "Backup protection format was not upgraded to authenticated-end-marker format."
Require-Pattern "src/EdgeRetails.Infrastructure/Production/Backup/AesGcmBackupProtector.cs" "Authenticated end" "Backup encrypted stream has no explicit authenticated end marker."
Require-Pattern "src/EdgeRetails.Infrastructure/Production/Backup/AesGcmBackupProtector.cs" "providerKey\.ToArray" "Backup protector may still zero provider-owned key memory."
Require-Pattern "src/EdgeRetails.Infrastructure/Production/Backup/HmacBackupManifestAuthenticator.cs" "HMACSHA256" "Backup manifest metadata is not authenticated."
Require-Pattern "src/EdgeRetails.Infrastructure/Production/Backup/BackupHistoryService.cs" "backup\.manifest_authentication_failed" "Backup history does not surface manifest authentication failures."
Require-Pattern "src/EdgeRetails.Infrastructure/Production/Backup/PostgresBackupEngine.cs" "RetentionWarningCode" "Backup success can still be falsified by post-commit retention failure."
Require-Pattern "src/EdgeRetails.Infrastructure/Production/Backup/PostgresBackupEngine.cs" "ProtectedPostgresTemp" "Plain PostgreSQL temp dumps are still staged inside the backup directory."
Require-Pattern "src/EdgeRetails.Application/Production/Diagnostics/ProductionDiagnostics.cs" "PostgresToolchain" "Diagnostics do not expose PostgreSQL client-tool readiness."
Require-Pattern "src/EdgeRetails.Application/Production/Diagnostics/ProductionDiagnostics.cs" "MaintenanceState" "Diagnostics do not expose production maintenance state."
Require-Pattern "src/EdgeRetails.Application/Production/Diagnostics/ProductionDiagnostics.cs" "WorkerHeartbeat" "Diagnostics do not expose worker heartbeat state."
Require-Pattern "src/EdgeRetails.Application/Production/Diagnostics/ProductionDiagnostics.cs" "DiskSpace" "Diagnostics do not expose disk-space state."
Require-Pattern "tests/EdgeRetails.UnitTests/Sprint8/Sprint8Phase2HardeningTests.cs" "InitialLicenseImport_WhenArtifactAlreadyExists_IsBlocked" "Missing first-setup-only license import regression test."
Require-Pattern "tests/EdgeRetails.UnitTests/Sprint8/Sprint8Phase2HardeningTests.cs" "PrintAuthorization_DenialOccursBeforeDocumentLoadOrPhysicalPrint" "Missing negative print authorization regression test."
Require-Pattern "tests/EdgeRetails.UnitTests/Sprint8/Sprint8Phase2HardeningTests.cs" "BackupEncryption_TruncationAtChunkBoundary_IsRejected" "Missing encrypted backup truncation regression test."
Require-Pattern "scripts/Verify-EfModelSync.ps1" "has-pending-model-changes" "Closure package lacks EF pending-model-change gate."

$source = Get-ChildItem (Join-Path $Root "src") -Recurse -File -Filter *.cs
$danger = $source | Select-String -Pattern 'DemoFallback|UseDemoBackend|FallbackToDemo' -CaseSensitive:$false
if ($danger) { $failures += "A demo/backend fallback marker exists in production source." }

$secretFiles = Get-ChildItem $Root -Recurse -File | Where-Object { $_.Extension -in @('.cs','.json','.config','.xml','.ps1','.wxs') }
$secretPatterns = @(
    'Password\s*=\s*"[^"\r\n]+"',
    'PGPASSWORD\s*=\s*"[^"\r\n]+"',
    'BEGIN\s+(RSA\s+)?PRIVATE\s+KEY'
)
foreach ($pattern in $secretPatterns) {
    $matches = $secretFiles | Select-String -Pattern $pattern -CaseSensitive:$false
    if ($matches) {
        $paths = ($matches.Path | Sort-Object -Unique) -join ", "
        $failures += "Potential persisted secret/private key matched '$pattern' in: $paths"
    }
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ }
    exit 1
}

Write-Host "Sprint 8 Phase 2 static forensic audit PASS"
