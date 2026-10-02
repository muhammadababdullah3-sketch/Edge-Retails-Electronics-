#Requires -RunAsAdministrator
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$probeRoot = Split-Path -Parent $PSScriptRoot
$probeExecutable = Join-Path $PSScriptRoot 'diagnostics\EdgeRetails.BackupFailureProbe\bin\Release\net10.0\EdgeRetails.BackupFailureProbe.exe'
$probeEvidenceRoot = Join-Path $probeRoot 'artifacts\master-remediation-20261002\backup-probe'
$probeStamp = [DateTime]::UtcNow.ToString('yyyyMMdd_HHmmss_fffZ')
$probeEvidencePath = Join-Path $probeEvidenceRoot "installed-capture-$probeStamp.log"
$probeExit = 1
New-Item -ItemType Directory -Path $probeEvidenceRoot -Force | Out-Null
Start-Transcript -LiteralPath $probeEvidencePath -NoClobber | Out-Null
try {
    $probeService = Get-CimInstance Win32_Service -Filter "Name='EdgeRetailsServer'"
    if ($null -eq $probeService -or $probeService.State -ne 'Running' -or
        $probeService.PathName.Trim('"') -cne 'C:\Program Files\Edge Retails\server\EdgeRetails.Server.exe' -or
        $probeService.StartName -cne 'LocalSystem') {
        throw 'Installed Server identity does not match the reviewed probe scope.'
    }
    if (-not (Test-Path -LiteralPath $probeExecutable -PathType Leaf)) { throw 'Reviewed probe is not built.' }
    Write-Output "START_UTC=$([DateTime]::UtcNow.ToString('o'))"
    Write-Output 'SCOPE=Read-only EventPipe exception observer; no raw trace/dump/environment capture; no service or configuration change.'
    & $probeExecutable $probeService.ProcessId 120
    $probeExit = $LASTEXITCODE
    Write-Output "PROBE_EXIT_CODE=$probeExit"
    Write-Output "END_UTC=$([DateTime]::UtcNow.ToString('o'))"
    Write-Output "CompletionStatus=$(if($probeExit -eq 0) { 'CAPTURED_REQUIRES_UI_CORRELATION' } else { 'FAIL_OR_NO_MATCH' })"
}
catch {
    Write-Output 'PROBE_WRAPPER_FAILED_SAFE'
    $probeExit = 1
}
finally { Stop-Transcript | Out-Null }
exit $probeExit
