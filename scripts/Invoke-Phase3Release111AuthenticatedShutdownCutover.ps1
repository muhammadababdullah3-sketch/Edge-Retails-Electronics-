#Requires -RunAsAdministrator
[CmdletBinding()]
param([switch]$AllowKnownOrphanCleanup)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$release = Join-Path $root 'artifacts\phase3c-recovery-release-20261001-22'
$evidenceDirectory = Join-Path $root 'artifacts\phase3-final-closure-20260930-01'
$stamp = [DateTime]::UtcNow.ToString('yyyyMMdd_HHmmss_fffZ')
$version = '1.0.11'
$maintenanceEntered = $false
$logPath = Join-Path $evidenceDirectory "phase3-release111-authenticated-shutdown-cutover-$stamp.log"
Start-Transcript -LiteralPath $logPath -NoClobber | Out-Null

function Invoke-ApprovedScript {
    param([Parameter(Mandatory)][string]$Name, [string[]]$Arguments = @())
    $path = Join-Path $PSScriptRoot $Name
    Write-Host "COMMAND=powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $path $($Arguments -join ' ')"
    $lines = @(& powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $path @Arguments 2>&1)
    $code = $LASTEXITCODE
    foreach ($line in $lines) { Write-Host $line }
    Write-Host "${Name}_EXIT_CODE=$code"
    if ($code -ne 0) { throw "$Name failed with exit code $code." }
    return $lines
}

function Assert-Service {
    param([string]$Name, [string]$State, [string]$StartMode)
    $service = Get-CimInstance Win32_Service -Filter "Name='$Name'"
    if ($null -eq $service -or $service.State -ne $State -or $service.StartMode -ne $StartMode) {
        throw "$Name state/start mode mismatch."
    }
    return $service
}

try {
    if (-not [string]::IsNullOrWhiteSpace($env:EDGE_RETAILS_DB)) { throw 'Database environment override is present.' }
    if ((Get-Service 'postgresql-x64-18').Status -ne 'Running') { throw 'PostgreSQL 18 is not running.' }
    if (@(Get-Process 'EdgeRetails.Recovery' -ErrorAction SilentlyContinue).Count -ne 0) {
        throw 'Close installed Recovery before the binary upgrade.'
    }
    $desktopAtEntry = @(Get-Process 'EdgeRetails.Desktop' -ErrorAction SilentlyContinue)
    if ($desktopAtEntry.Count -gt 0 -and -not $AllowKnownOrphanCleanup) {
        throw 'Desktop remains active; explicit known-orphan cleanup is required for the proven shutdown defect.'
    }
    $manifestPath = Join-Path $release 'release-manifest.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw 'Approved release manifest is missing.' }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.version -cne $version) { throw 'Approved release version mismatch.' }
    $setupPath = Join-Path $release 'setup\EdgeRetailsSetup.exe'
    $setupItem = @($manifest.artifacts | Where-Object { $_.File -ceq 'EdgeRetailsSetup.exe' })
    if ($setupItem.Count -ne 1 -or (Get-FileHash -LiteralPath $setupPath -Algorithm SHA256).Hash -cne $setupItem[0].Sha256 -or
        $setupItem[0].Sha256 -cne 'AF2643117727176D4C3F04B6D39724EDD8FF9D42C45C6493C684D7EE17E9B09C') {
        throw 'Approved installer hash mismatch.'
    }
    foreach ($name in @('EdgeRetailsServer','EdgeRetailsWorker')) {
        $service = Assert-Service -Name $name -State 'Running' -StartMode 'Auto'
        $expected = if ($name -eq 'EdgeRetailsServer') { 'C:\Program Files\Edge Retails\server\EdgeRetails.Server.exe' } else { 'C:\Program Files\Edge Retails\worker\EdgeRetails.Worker.exe' }
        if ($service.PathName.Trim('"') -cne $expected -or $service.StartName -cne 'LocalSystem') { throw "$name installed authority mismatch." }
    }
    $priorManifest = Get-Content -LiteralPath (Join-Path $root 'artifacts\phase3c-recovery-release-20260930-21\release-manifest.json') -Raw | ConvertFrom-Json
    if ($priorManifest.version -cne '1.0.10') { throw 'Prior certified manifest version mismatch.' }
    $installedComponents = @{
        'EdgeRetails.Desktop.exe' = 'C:\Program Files\Edge Retails\EdgeRetails.Desktop.exe'
        'EdgeRetails.Server.exe' = 'C:\Program Files\Edge Retails\server\EdgeRetails.Server.exe'
        'EdgeRetails.Worker.exe' = 'C:\Program Files\Edge Retails\worker\EdgeRetails.Worker.exe'
        'EdgeRetails.Recovery.exe' = 'C:\Program Files\Edge Retails\recovery\EdgeRetails.Recovery.exe'
    }
    foreach ($file in $installedComponents.Keys) {
        $priorArtifact = @($priorManifest.artifacts | Where-Object { $_.File -ceq $file })
        if ($priorArtifact.Count -ne 1 -or (Get-FileHash -LiteralPath $installedComponents[$file] -Algorithm SHA256).Hash -cne $priorArtifact[0].Sha256 -or
            [Diagnostics.FileVersionInfo]::GetVersionInfo($installedComponents[$file]).FileVersion -cne '1.0.10') {
            throw 'Installed starting release differs from the preserved 1.0.10 checkpoint.'
        }
    }
    if ($desktopAtEntry.Count -gt 0) {
        if ($desktopAtEntry.Count -ne 1) { throw 'Unexpected Desktop process count; no orphan cleanup performed.' }
        $orphan = Get-Process -Id $desktopAtEntry[0].Id -ErrorAction Stop
        if ($orphan.Id -ne 13064 -or $orphan.MainWindowHandle -ne 0 -or
            $orphan.StartTime.ToUniversalTime().ToString('o') -cne '2026-10-01T03:36:18.6302566Z' -or
            $orphan.Path -ine 'C:\Program Files\Edge Retails\EdgeRetails.Desktop.exe' -or
            (Get-FileHash -LiteralPath $orphan.Path -Algorithm SHA256).Hash -cne 'F70AA3D3BB7EDCAB85C5F1696C4EA4F76A9F369AAF7450ED7D2513DADB42BE8B') {
            throw 'Desktop process is not the exact observed candidate21 orphan; no cleanup performed.'
        }
        Stop-Process -Id $orphan.Id -ErrorAction Stop
        if (-not $orphan.WaitForExit(5000)) { throw 'Known Desktop orphan did not exit after cleanup.' }
        Write-Output 'KNOWN_ORPHAN_CLEANUP_PASS Pid=13064; not a normal-close proof; original installed close FAIL preserved.'
    }
    if (@(Get-Process 'EdgeRetails.Desktop','EdgeRetails.Recovery' -ErrorAction SilentlyContinue).Count -ne 0) {
        throw 'Desktop or Recovery process remains; binary upgrade not allowed.'
    }
    $protectedPaths = @(
        'C:\ProgramData\EdgeRetails\config.json',
        'C:\ProgramData\EdgeRetails\license.erlic',
        'C:\ProgramData\EdgeRetails\recovery\trust.json'
    )
    $protectedHashes = @{}
    foreach ($path in $protectedPaths) { $protectedHashes[$path] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
    if ($protectedHashes['C:\ProgramData\EdgeRetails\license.erlic'] -cne '0C1C66079700B26C6ACEB0E8C56F998040894B0C3AD2E791AA2574295946710D' -or
        $protectedHashes['C:\ProgramData\EdgeRetails\recovery\trust.json'] -cne 'F26B7CF12322B1F4C22BD3A40498CF77E23AD6336074B95F131E902A2A54BF73') {
        throw 'Recovery trust or signed license differs from the certified checkpoint.'
    }
    Write-Output "START_UTC=$([DateTime]::UtcNow.ToString('o'))"
    Write-Output 'Provider=PostgreSQL 18 / Npgsql'
    Write-Output "Release=$version Candidate=-22 SetupSHA256=$($setupItem[0].Sha256)"

    $maintenanceEntered = $true
    [void](Invoke-ApprovedScript -Name 'Stop-Phase3Release106Maintenance.ps1')
    foreach ($name in @('EdgeRetailsServer','EdgeRetailsWorker')) { Set-Service -Name $name -StartupType Disabled }
    [void](Assert-Service -Name 'EdgeRetailsServer' -State 'Stopped' -StartMode 'Disabled')
    [void](Assert-Service -Name 'EdgeRetailsWorker' -State 'Stopped' -StartMode 'Disabled')
    if (Get-NetTCPConnection -State Listen -LocalPort 7150 -ErrorAction SilentlyContinue) { throw 'Loopback listener remains in maintenance.' }
    Write-Output 'MAINTENANCE_HOLD_PASS'

    $backupLines = @(Invoke-ApprovedScript -Name 'Invoke-Phase3Release106BackupUpgradeRehearsal.ps1')
    $backupMarker = @($backupLines | Where-Object { [string]$_ -match '^PHASE3_RELEASE106_BACKUP_UPGRADE_REHEARSAL_PASS Evidence=' })
    if ($backupMarker.Count -ne 1 -or $backupMarker[0] -notmatch '^PHASE3_RELEASE106_BACKUP_UPGRADE_REHEARSAL_PASS Evidence=(.+?) Backup=(.+?) SHA256=([A-Fa-f0-9]{64})$') {
        throw 'Fresh backup PASS marker was not verified.'
    }
    $backupEvidencePath = $Matches[1]
    $backupEvidence = Get-Content -LiteralPath $backupEvidencePath -Raw | ConvertFrom-Json
    if ($backupEvidence.CompletionStatus -cne 'PASS' -or $backupEvidence.Provider -cne 'PostgreSQL 18 / Npgsql' -or
        $backupEvidence.Backup.RestoredExactArchivePass -ne $true -or $backupEvidence.Rehearsal.CleanupStatus -cne 'PASS') {
        throw 'Fresh backup/restore evidence is incomplete.'
    }
    if ($backupEvidence.MigrationIds.ProductionBaseline.Count -ne 20 -or $backupEvidence.MigrationIds.Final.Count -ne 20 -or
        $backupEvidence.Rehearsal.FinalHistoryCount -ne 20 -or $backupEvidence.Rehearsal.ProductionAlreadyAtFinalMigration20 -ne $true -or
        (Get-FileHash -LiteralPath $backupEvidence.Backup.Path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $backupEvidence.Backup.Sha256) {
        throw 'Fresh backup hash or final migration state differs from the approved checkpoint.'
    }
    Write-Output "FRESH_BACKUP_RESTORE_PASS Evidence=$backupEvidencePath SHA256=$($backupEvidence.Backup.Sha256)"
    [void](Invoke-ApprovedScript -Name 'Test-Phase3Release106ProductionSchema.ps1' -Arguments @('-BackupEvidencePath', $backupEvidencePath))

    $installEvidence = Join-Path $evidenceDirectory "release-1.0.11-install-$stamp.json"
    [void](Invoke-ApprovedScript -Name 'Invoke-Phase3InstallRelease104.ps1' -Arguments @(
        '-ReleaseVersion', $version, '-ReleaseRoot', $release, '-EvidencePath', $installEvidence))
    $installResult = Get-Content -LiteralPath $installEvidence -Raw | ConvertFrom-Json
    if ($installResult.Status -cne 'PASS' -or $installResult.ReleaseVersion -cne $version -or $installResult.InstallerExitCode -ne 0) {
        throw 'Installer terminal evidence is not PASS.'
    }
    foreach ($path in $protectedPaths) {
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -cne $protectedHashes[$path]) {
            throw 'Protected runtime configuration, signed license, or Recovery trust changed.'
        }
    }
    Write-Output "INSTALL_AND_PROTECTED_AUTHORITY_PASS Evidence=$installEvidence"

    $recoveryBinary = 'C:\Program Files\Edge Retails\recovery\EdgeRetails.Recovery.exe'
    $recoveryManifest = @($manifest.artifacts | Where-Object { $_.File -ceq 'EdgeRetails.Recovery.exe' })
    if ($recoveryManifest.Count -ne 1 -or (Get-FileHash -LiteralPath $recoveryBinary -Algorithm SHA256).Hash -cne $recoveryManifest[0].Sha256 -or
        [Diagnostics.FileVersionInfo]::GetVersionInfo($recoveryBinary).FileVersion -cne $version) {
        throw 'Installed Recovery binary does not match the approved release.'
    }
    Set-Service -Name 'EdgeRetailsServer' -StartupType Automatic
    Start-Service -Name 'EdgeRetailsServer'
    (Get-Service 'EdgeRetailsServer').WaitForStatus('Running', [TimeSpan]::FromSeconds(60))
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    $ready = $null
    do {
        try { $ready = Invoke-RestMethod -Uri 'http://127.0.0.1:7150/api/system/ready' -TimeoutSec 3 } catch { $ready = $null }
        if ($null -ne $ready -and $ready.status -ceq 'Ready' -and $ready.canConnect -and -not $ready.hasPendingMigrations -and $ready.maintenanceState -ceq 'Normal') { break }
        Start-Sleep -Milliseconds 750
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($null -eq $ready -or $ready.status -cne 'Ready' -or -not $ready.canConnect -or $ready.hasPendingMigrations -or $ready.maintenanceState -cne 'Normal') {
        throw 'Installed Server readiness failed.'
    }
    $server = Assert-Service -Name 'EdgeRetailsServer' -State 'Running' -StartMode 'Auto'
    $listeners = @(Get-NetTCPConnection -State Listen -LocalPort 7150 -ErrorAction SilentlyContinue)
    if ($listeners.Count -lt 1 -or @($listeners | Where-Object { $_.LocalAddress -cne '127.0.0.1' -or $_.OwningProcess -ne $server.ProcessId }).Count -gt 0) {
        throw 'Installed Server listener is not loopback-only.'
    }
    [void](Invoke-ApprovedScript -Name 'Test-EdgeRetailsServices.ps1')
    Write-Output 'SERVER_READY HTTP=200 Address=127.0.0.1:7150 MigrationPending=False Maintenance=Normal'
    $trust = Get-Content -LiteralPath 'C:\ProgramData\EdgeRetails\recovery\trust.json' -Raw | ConvertFrom-Json
    $context = Invoke-RestMethod -Uri 'http://127.0.0.1:7150/api/recovery/context' -TimeoutSec 5
    if ([string]::IsNullOrWhiteSpace($context.licenseId) -or [string]::IsNullOrWhiteSpace($context.deviceId) -or
        [string]::IsNullOrWhiteSpace($context.recoveryIssuerId) -or $context.recoveryIssuerId -cne $trust.RecoveryAuthorization.IssuerId) {
        throw 'Installed Recovery context does not match protected trust.'
    }
    Write-Output 'RECOVERY_CONTEXT_PASS HTTP=200 LicenseBound=True DeviceBound=True IssuerMatchesProtectedTrust=True'

    Set-Service -Name 'EdgeRetailsWorker' -StartupType Automatic
    Start-Service -Name 'EdgeRetailsWorker'
    (Get-Service 'EdgeRetailsWorker').WaitForStatus('Running', [TimeSpan]::FromSeconds(60))
    $worker = Assert-Service -Name 'EdgeRetailsWorker' -State 'Running' -StartMode 'Auto'
    $workerPid = $worker.ProcessId
    Start-Sleep -Seconds 12
    $worker = Assert-Service -Name 'EdgeRetailsWorker' -State 'Running' -StartMode 'Auto'
    if ($worker.ProcessId -ne $workerPid) { throw 'Worker restarted during stability observation.' }
    $workerConnections = @(Get-NetTCPConnection -State Established -OwningProcess $workerPid -RemotePort 5432 -ErrorAction SilentlyContinue |
        Where-Object { $_.RemoteAddress -eq '127.0.0.1' })
    if ($workerConnections.Count -lt 1) { throw 'Worker PostgreSQL loopback connection was not observed.' }
    Write-Output 'WORKER_POSTGRESQL18_LOOPBACK_CONNECTION_PASS'
    $workerRecovery = (& sc.exe qfailure EdgeRetailsWorker) -join "`n"
    if ($LASTEXITCODE -ne 0 -or [regex]::Matches($workerRecovery, 'RESTART', [Text.RegularExpressions.RegexOptions]::IgnoreCase).Count -lt 3 -or
        $workerRecovery -notmatch '60000') { throw 'Worker failure recovery policy mismatch.' }
    Write-Output 'WORKER_FAILURE_RECOVERY_PASS Restarts=3 DelayMilliseconds=60000'
    [void](Invoke-ApprovedScript -Name 'Test-EdgeRetailsServices.ps1')
    Write-Output "WORKER_RUNNING Auto=True Pid=$workerPid StableForSeconds=12"
    Write-Output 'EXIT_CODE=0'
    Write-Output 'CompletionStatus=PASS'
    exit 0
}
catch {
    Write-Output "FAILURE=$($_.Exception.Message)"
    if ($maintenanceEntered) {
        $holdConfirmed = $true
        foreach ($name in @('EdgeRetailsWorker','EdgeRetailsServer')) {
            try { Set-Service -Name $name -StartupType Disabled }
            catch { $holdConfirmed = $false; Write-Output "HOLD_DISABLE_FAILED=$name" }
            try {
                if ((Get-Service $name).Status -ne 'Stopped') { Stop-Service -Name $name -ErrorAction Stop }
                (Get-Service $name).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
            }
            catch { $holdConfirmed = $false; Write-Output "HOLD_STOP_FAILED=$name" }
            try {
                [void](Assert-Service -Name $name -State 'Stopped' -StartMode 'Disabled')
                Write-Output "FAIL_CLOSED_HOLD=$name`:Stopped/Disabled"
            }
            catch { $holdConfirmed = $false; Write-Output "HOLD_FAILED=$name" }
        }
        if (Get-NetTCPConnection -State Listen -LocalPort 7150 -ErrorAction SilentlyContinue) { $holdConfirmed = $false; Write-Output 'HOLD_FAILED=Listener7150StillPresent' }
        Write-Output "FAIL_CLOSED_HOLD_STATUS=$(if ($holdConfirmed) { 'PASS' } else { 'FAIL' })"
    }
    Write-Output 'CompletionStatus=FAIL'
    Write-Output 'EXIT_CODE=1'
    exit 1
}
finally {
    Stop-Transcript | Out-Null
}
