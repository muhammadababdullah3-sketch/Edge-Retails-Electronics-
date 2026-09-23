param([string]$Root = ".")
$ErrorActionPreference = "Stop"
$failures = @()

function Require-Contains([string]$Path, [string]$Pattern, [string]$Message) {
    $content = Get-Content $Path -Raw
    if ($content -notmatch $Pattern) { $script:failures += $Message }
}
function Require-NotContains([string]$Path, [string]$Pattern, [string]$Message) {
    $content = Get-Content $Path -Raw
    if ($content -match $Pattern) { $script:failures += $Message }
}

$contracts = Join-Path $Root "src/EdgeRetails.Application/Production/Backup/BackupContracts.cs"
$handlers = Join-Path $Root "src/EdgeRetails.Application/Production/Backup/BackupHandlers.cs"
$security = Join-Path $Root "src/EdgeRetails.Application/Production/ProductionSecurity.cs"
$engine = Join-Path $Root "src/EdgeRetails.Infrastructure/Production/Backup/PostgresBackupEngine.cs"
$history = Join-Path $Root "src/EdgeRetails.Infrastructure/Production/Backup/BackupHistoryService.cs"
$pathSafety = Join-Path $Root "src/EdgeRetails.Infrastructure/Production/Backup/BackupArtifactPathSafety.cs"
$journal = Join-Path $Root "src/EdgeRetails.Infrastructure/Production/Backup/HmacRestoreSessionStore.cs"
$validator = Join-Path $Root "src/EdgeRetails.Infrastructure/Production/Backup/CanonicalRestoreStagingValidator.cs"
$printHandlers = Join-Path $Root "src/EdgeRetails.Application/Production/Printing/PrintingHandlers.cs"
$licenseHandlers = Join-Path $Root "src/EdgeRetails.Application/Production/Licensing/LicenseHandlers.cs"
$phase1Tests = Join-Path $Root "tests/EdgeRetails.UnitTests/Sprint8/Sprint8Phase1SafetyTests.cs"
$cutoverTests = Join-Path $Root "tests/EdgeRetails.UnitTests/Sprint8/Sprint8RestoreCutoverSafetyTests.cs"

@($contracts,$handlers,$security,$engine,$history,$pathSafety,$journal,$validator,$phase1Tests,$cutoverTests) | ForEach-Object {
    if (-not (Test-Path $_)) { $failures += "Missing Phase 1 artifact: $_" }
}

Require-NotContains $contracts 'record\s+RestorePrepared' "Caller-controlled RestorePrepared type still exists."
Require-Contains $contracts 'record\s+RestoreSessionToken\s*\(Guid\s+RestoreId\)' "Opaque restore-session token contract missing."
Require-Contains $contracts 'IPostgresMaintenanceConnectionProvider' "Recovery-only PostgreSQL credential provider contract missing."
Require-Contains $journal 'HMACSHA256' "Restore journal is not integrity protected with HMAC."
Require-Contains $pathSafety 'Path\.GetFullPath' "Backup canonical-path containment is missing."
Require-Contains $pathSafety 'ReparsePoint' "Backup reparse-point rejection is missing."
Require-NotContains $history 'HashSet<Guid>' "Retention still selects deletion by manifest-controlled BackupId."
Require-Contains $history 'BackupArtifactPathSafety\.ResolveOwnedBackupPath' "Retention is not bound to canonical backup artifacts."
Require-Contains $engine 'OriginalDatabaseOid' "Cutover does not persist original database OID authority."
Require-Contains $engine 'StagingDatabaseOid' "Cutover does not persist staging database OID authority."
Require-Contains $engine 'ALLOW_CONNECTIONS' "Database-level reconnect barrier is missing."
Require-Contains $engine 'CutoverSafetyTimeout' "Bounded cancellation-independent cutover safety token is missing."
Require-Contains $engine 'RollBackByDatabaseOidAsync' "OID-based rollback reconciliation is missing."
Require-Contains $engine 'RestoreRecoveryRequiredException' "Fail-closed recovery-required state is missing."
Require-Contains $engine 'Recovery identity must be distinct' "Runtime and recovery PostgreSQL identities are not enforced as distinct."
Require-Contains $validator 'RequiredSchemas' "Canonical staging schema validation is missing."
Require-Contains $validator 'requires at least one live migration/business compatibility probe' "Staging validation can be configured without a live compatibility probe."
Require-Contains $security 'ProductionMaintenanceWriteGuard' "Application write-guard seam is missing."
Require-Contains $security 'AppendAfterSideEffectAsync' "Post-side-effect audit outcome isolation is missing."
Require-NotContains $handlers '_audit\.AppendAsync' "Backup/restore handlers can still throw directly from post-side-effect audit persistence."
Require-NotContains $printHandlers '_audit\.AppendAsync' "Printing handler can still falsify print result through audit persistence."
Require-NotContains $licenseHandlers '_audit\.AppendAsync' "License handler can still falsify persisted-license result through audit persistence."
Require-Contains $phase1Tests 'Retention_CorruptTraversalManifest_NeverDeletesOutsideBackupRoot' "Traversal retention regression test missing."
Require-Contains $phase1Tests 'RestoreJournal_TamperingFailsClosed' "Restore-journal tamper regression test missing."
Require-Contains $cutoverTests 'AmbiguousFailureAfterFirstRename' "First-rename ambiguous failure rollback test missing."
Require-Contains $cutoverTests 'AmbiguousFailureAfterSecondRename' "Second-rename ambiguous failure rollback test missing."
Require-Contains $cutoverTests 'CallerCancellationAfterDestructivePhaseBegins' "Caller-cancellation safety test missing."

if ($failures.Count) {
    $failures | ForEach-Object { Write-Error $_ }
    exit 1
}
Write-Host "Sprint 8 Phase 1 static forensic audit PASS"
