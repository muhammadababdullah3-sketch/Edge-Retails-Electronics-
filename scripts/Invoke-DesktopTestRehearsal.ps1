[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$EvidenceDirectory,
    [string]$Project = 'tests/EdgeRetails.Desktop.PerformanceTests/EdgeRetails.Desktop.PerformanceTests.csproj',
    [string]$Configuration = 'Release',
    [string]$Filter
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = Split-Path -Parent $PSScriptRoot
$repoRoot = [IO.Path]::GetFullPath($repoRoot)
$evidenceRoot = [IO.Path]::GetFullPath($EvidenceDirectory)
$runRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'governance/10-execution-runs'))
if (-not $evidenceRoot.StartsWith($runRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'DESKTOP_TEST_EVIDENCE_MUST_BE_UNDER_GOVERNANCE_RUN_FOLDER'
}
if (Test-Path -LiteralPath $evidenceRoot) {
    throw 'DESKTOP_TEST_EVIDENCE_DIRECTORY_ALREADY_EXISTS'
}
New-Item -ItemType Directory -Path $evidenceRoot -Force | Out-Null
$stateRoot = Join-Path $evidenceRoot 'isolated-state'
New-Item -ItemType Directory -Path $stateRoot | Out-Null
$markerPath = Join-Path $stateRoot 'OWNERSHIP_MARKER.json'
$marker = [pscustomobject]@{
    marker = 'EDGE_RETAILS_DISPOSABLE_DESKTOP_TEST_STATE'
    evidence_directory = $evidenceRoot
    process_id = $PID
    state_directory = $stateRoot
    created_utc = [DateTime]::UtcNow.ToString('o')
}
$marker | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $markerPath -Encoding utf8

$envNames = @('EDGE_RETAILS_STATE_DIR', 'EDGE_RETAILS_PRODUCTION_STATE_DIR')
$oldEnv = @{}
foreach ($name in $envNames) { $oldEnv[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnet = if ($null -ne $dotnetCommand) { $dotnetCommand.Source } else { $null }
if (-not $dotnet) {
    $profileRoot = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
    $localDotnet = Join-Path $profileRoot '.dotnet/dotnet.exe'
    if (Test-Path -LiteralPath $localDotnet) { $dotnet = $localDotnet }
}
if (-not $dotnet) { throw 'DOTNET_SDK_NOT_FOUND' }

$exitCode = 1
try {
    [Environment]::SetEnvironmentVariable('EDGE_RETAILS_STATE_DIR', $stateRoot, 'Process')
    [Environment]::SetEnvironmentVariable('EDGE_RETAILS_PRODUCTION_STATE_DIR', $null, 'Process')
    $arguments = @('test', $Project, '--configuration', $Configuration, '--logger', 'trx;LogFileName=desktop-current.trx', '--results-directory', $evidenceRoot)
    if (-not [string]::IsNullOrWhiteSpace($Filter)) { $arguments += @('--filter', $Filter) }
    Push-Location -LiteralPath $repoRoot
    try {
        & $dotnet @arguments 2>&1 | Tee-Object -FilePath (Join-Path $evidenceRoot 'desktop-test-console.log')
        $exitCode = $LASTEXITCODE
    }
    finally { Pop-Location }
}
finally {
    foreach ($name in $envNames) { [Environment]::SetEnvironmentVariable($name, $oldEnv[$name], 'Process') }
    [pscustomobject]@{
        runner = 'Invoke-DesktopTestRehearsal.ps1'
        dotnet = $dotnet
        project = $Project
        configuration = $Configuration
        filter = $Filter
        exit_code = $exitCode
        status = if ($exitCode -eq 0) { 'PASS' } else { 'FAIL' }
        state_directory = $stateRoot
        state_directory_isolated = $true
        environment_restored = $true
        completed_utc = [DateTime]::UtcNow.ToString('o')
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $evidenceRoot 'desktop-test-result.json') -Encoding utf8
}
exit $exitCode
