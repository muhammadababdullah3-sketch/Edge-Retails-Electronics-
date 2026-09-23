param([Parameter(Mandatory=$true)][string]$PgBin)
$ErrorActionPreference = "Stop"
$required = @("pg_dump.exe", "pg_restore.exe", "psql.exe", "createdb.exe")
foreach ($name in $required) {
    $path = Join-Path $PgBin $name
    if (-not (Test-Path $path -PathType Leaf)) { throw "Required PostgreSQL client tool is missing: $path" }
    $version = & $path --version
    if ($LASTEXITCODE -ne 0) { throw "Unable to execute PostgreSQL client tool: $path" }
    Write-Host "$name : $version"
}
Write-Host "PostgreSQL client readiness PASS"
