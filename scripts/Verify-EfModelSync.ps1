param(
    [Parameter(Mandatory = $true)][string]$Project,
    [Parameter(Mandatory = $true)][string]$StartupProject,
    [string]$Context
)

$ErrorActionPreference = 'Stop'

$arguments = @('ef', 'migrations', 'has-pending-model-changes', '--project', $Project, '--startup-project', $StartupProject)
if (-not [string]::IsNullOrWhiteSpace($Context)) {
    $arguments += @('--context', $Context)
}

Write-Host 'Verifying EF model snapshot synchronization...'
& dotnet @arguments
if ($LASTEXITCODE -ne 0) {
    throw 'EF Core reports pending model changes or the model-sync verification could not complete.'
}

Write-Host 'EF model snapshot synchronization verification passed.'
