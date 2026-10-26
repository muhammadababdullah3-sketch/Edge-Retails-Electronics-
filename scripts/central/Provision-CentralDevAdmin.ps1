param(
    [string] $Username,
    [string] $Password
)

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$envFile = Join-Path $repoRoot 'local\central-dev.env.ps1'
$dotEnv = Join-Path $repoRoot 'local\central-dev.env'

if (Test-Path $envFile) { . $envFile }
elseif (Test-Path $dotEnv) {
    Get-Content -LiteralPath $dotEnv | ForEach-Object {
        $line = $_.Trim()
        if ($line.Length -eq 0 -or $line.StartsWith('#')) { return }
        $eq = $line.IndexOf('=')
        if ($eq -le 0) { return }
        $name = $line.Substring(0, $eq).Trim()
        $value = $line.Substring($eq + 1).Trim()
        if ([string]::IsNullOrEmpty([Environment]::GetEnvironmentVariable($name))) {
            Set-Item -Path "Env:$name" -Value $value
        }
    }
}
else {
    Write-Error "Missing local/central-dev.env — run Initialize-CentralLocalDisposable.ps1 first."
}

if ($Username) { $env:CENTRAL_DEV_PORTAL_USERNAME = $Username }
if ($Password) { $env:CENTRAL_DEV_PORTAL_PASSWORD = $Password }

$devTools = Join-Path $repoRoot 'src\EdgeRetails.Central.DevTools\EdgeRetails.Central.DevTools.csproj'
Set-Location $repoRoot
dotnet run --project $devTools -- sync-portal-admin
