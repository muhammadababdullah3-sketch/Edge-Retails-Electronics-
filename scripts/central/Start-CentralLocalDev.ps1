$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$ps1Env = Join-Path $repoRoot 'local\central-dev.env.ps1'
$dotEnv = Join-Path $repoRoot 'local\central-dev.env'

if (Test-Path $ps1Env) {
    . $ps1Env
}
elseif (Test-Path $dotEnv) {
    $env:CENTRAL_LOCAL_ENV_FILE = $dotEnv
    Get-Content -LiteralPath $dotEnv | ForEach-Object {
        $line = $_.Trim()
        if ($line.Length -eq 0 -or $line.StartsWith('#')) { return }
        $eq = $line.IndexOf('=')
        if ($eq -le 0) { return }
        $name = $line.Substring(0, $eq).Trim()
        $value = $line.Substring($eq + 1).Trim()
        if ($value.Length -ge 2 -and $value.StartsWith('"') -and $value.EndsWith('"')) {
            $value = $value.Substring(1, $value.Length - 2)
        }
        if ([string]::IsNullOrEmpty([Environment]::GetEnvironmentVariable($name))) {
            Set-Item -Path "Env:$name" -Value $value
        }
    }
}
else {
    Write-Error @"
Missing local development environment.
Copy local/central-dev.env.example to local/central-dev.env OR local/central-dev.env.ps1.example to local/central-dev.env.ps1
and fill attested values (see docs/central/OPERATIONS_AND_RUNBOOK.md).
"@
}

$env:ASPNETCORE_ENVIRONMENT = 'Development'

$devTools = Join-Path $repoRoot 'src\EdgeRetails.Central.DevTools\EdgeRetails.Central.DevTools.csproj'
Write-Host 'Validating Central minimum host environment...'
dotnet run --project $devTools -- check-env
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$portalRecord = Join-Path $repoRoot 'local\central-dev.portal.json'
if (-not (Test-Path $portalRecord)) {
    Write-Host 'No local/central-dev.portal.json — running sync-portal-admin once...'
    dotnet run --project $devTools -- sync-portal-admin
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

$apiProject = Join-Path $repoRoot 'src\EdgeRetails.Central.Api\EdgeRetails.Central.Api.csproj'
Write-Host "Starting Central.Api from $repoRoot"
Set-Location $repoRoot
dotnet run --project $apiProject -c Release --no-launch-profile
