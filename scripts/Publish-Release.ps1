param(
    [Parameter(Mandatory=$true)][string]$Version,
    [string]$Runtime = "win-x64",
    [string]$Solution = "EdgeRetails.sln",
    [string]$DesktopProject = "src/EdgeRetails.Desktop/EdgeRetails.Desktop.csproj",
    [string]$WorkerProject = "src/EdgeRetails.Worker/EdgeRetails.Worker.csproj",
    [string]$ServerProject = "src/EdgeRetails.Server/EdgeRetails.Server.csproj",
    [string]$UnitTestProject = "tests/EdgeRetails.UnitTests/EdgeRetails.UnitTests.csproj",
    [string]$Output = "artifacts/release",
    [string]$PgBin = "",
    [string]$FingerprintPath = "",
    [string]$PreflightEvidencePath = "",
    [string]$CanonicalArchitectureSha = "12344760C60124DDC2D1C0E54ABFB7BC0E82B9B23A46E6463303EBFA530AB673"
)
$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Installer-safe Version must use major.minor.patch (for example 1.0.0)." }
$versionParts = $Version.Split('.') | ForEach-Object { [int]$_ }
if ($versionParts[0] -gt 255 -or $versionParts[1] -gt 255 -or $versionParts[2] -gt 65535) { throw "MSI ProductVersion requires major/minor <= 255 and build <= 65535." }
if ($Runtime -notmatch '^win-(x64|arm64)$') { throw "Only supported Windows production RIDs are accepted by this release pipeline." }
$wixPlatform = if ($Runtime -eq 'win-arm64') { 'arm64' } else { 'x64' }
$root = (Resolve-Path ".").Path
$publish = Join-Path $root "$Output/publish"
$msiOut = Join-Path $root "$Output/msi"
$bundleOut = Join-Path $root "$Output/setup"

$fingerprint = $null
$preflightHash = $null
if (-not [string]::IsNullOrWhiteSpace($FingerprintPath) -and -not [string]::IsNullOrWhiteSpace($PreflightEvidencePath)) {
    & "$PSScriptRoot/Test-Sprint8Phase4PreflightEvidence.ps1" `
        -Root $root -FingerprintPath $FingerprintPath `
        -EvidencePath $PreflightEvidencePath -RequirePostgresClosure

    $fingerprint = Get-Content ([IO.Path]::GetFullPath($FingerprintPath)) -Raw | ConvertFrom-Json
    $preflightHash = (Get-FileHash ([IO.Path]::GetFullPath($PreflightEvidencePath)) -Algorithm SHA256).Hash
}
else {
    $canonicalPath = Join-Path $root "docs/Edge_Retails_Final_Architecture_Report_v1.md"
    if (-not (Test-Path $canonicalPath -PathType Leaf)) { throw "Canonical architecture report missing: $canonicalPath" }
    $actualCanonicalSha = (Get-FileHash $canonicalPath -Algorithm SHA256).Hash
    if ($actualCanonicalSha -ne $CanonicalArchitectureSha) {
        throw "Canonical Architecture SHA mismatch: expected $CanonicalArchitectureSha, actual $actualCanonicalSha"
    }
    & "$PSScriptRoot/Verify-ArchitectureInternationalAuditRemediation.ps1" -Root $root
}

$outputFull = [IO.Path]::GetFullPath((Join-Path $root $Output))
if ($outputFull -eq $root -or $outputFull.Length -le $root.Length) {
    throw "Output directory cannot be root or workspace root."
}
if (Test-Path $outputFull) { Remove-Item $outputFull -Recurse -Force }
New-Item -ItemType Directory -Force -Path $publish,$msiOut,$bundleOut | Out-Null

# General release verification is RID-neutral. PostgreSQL integration evidence is supplied by the
# fingerprint-bound Phase 4 preflight above; do not execute environment-dependent integration tests unprovisioned.
dotnet restore $Solution
if ($LASTEXITCODE -ne 0) { throw "Solution restore failed." }

dotnet clean $Solution -c Release -v minimal
if ($LASTEXITCODE -ne 0) { throw "Release clean failed." }

# Do not use repository-wide formatting as a release authority. The repository contains
# reviewed legacy/generated style debt from already-closed work; Sprint 8 uses touched-scope
# review plus compiler/test/static gates to avoid unrelated source churn.
Write-Host "Repository-wide format gate intentionally omitted; touched-scope formatting policy applies."

dotnet build $Solution -c Release --no-restore -warnaserror
if ($LASTEXITCODE -ne 0) { throw "Release solution build failed." }

dotnet test $UnitTestProject -c Release --no-build --no-restore
if ($LASTEXITCODE -ne 0) { throw "Release unit-test gate failed." }

dotnet restore $DesktopProject -r $Runtime
if ($LASTEXITCODE -ne 0) { throw "Desktop runtime restore failed." }

# Require a real application icon from the live project. Sprint 8 must not invent product branding.
[xml]$desktopXml = Get-Content $DesktopProject
$iconValue = @(
    $desktopXml.Project.PropertyGroup |
        ForEach-Object { [string]$_.ApplicationIcon } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
) | Select-Object -First 1
$iconPath = $null
if ($iconValue) { $iconPath = Join-Path (Split-Path $DesktopProject -Parent) $iconValue }
if ($iconPath -and (Test-Path $iconPath -PathType Leaf)) { $iconPath = (Resolve-Path $iconPath).Path }
if (-not $iconPath -or -not (Test-Path $iconPath -PathType Leaf)) {
    throw "Release requires an existing ApplicationIcon in the live Desktop project. Do not ship an unbranded/default executable."
}

$publishArgs = @(
    "publish", $DesktopProject, "-c", "Release", "-r", $Runtime, "--self-contained", "true", "--no-restore",
    "-p:Version=$Version", "-p:FileVersion=$Version", "-p:AssemblyVersion=$Version",
    "-p:Product=Edge Retails", "-p:Company=Edge Retails", "-p:Description=Edge Retails Electronics Point of Sale",
    "-p:ApplicationIcon=$iconPath", "-p:PublishSingleFile=false", "-p:DebugType=None", "-p:DebugSymbols=false",
    "-p:Deterministic=true", "-p:ContinuousIntegrationBuild=true", "-p:TreatWarningsAsErrors=true",
    "-o", $publish
)
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "Desktop publish failed." }

$exe = Join-Path $publish "EdgeRetails.Desktop.exe"
if (-not (Test-Path $exe -PathType Leaf)) { throw "Publish did not produce EdgeRetails.Desktop.exe." }

# Publish Worker
dotnet restore $WorkerProject -r $Runtime
if ($LASTEXITCODE -ne 0) { throw "Worker runtime restore failed." }
$workerPublish = Join-Path $publish "worker"
$workerArgs = @(
    "publish", $WorkerProject, "-c", "Release", "-r", $Runtime, "--self-contained", "true", "--no-restore",
    "-p:Version=$Version", "-p:FileVersion=$Version", "-p:AssemblyVersion=$Version",
    "-p:Product=Edge Retails Worker", "-p:Company=Edge Retails", "-p:Description=Edge Retails Background Worker",
    "-p:PublishSingleFile=false", "-p:DebugType=None", "-p:DebugSymbols=false",
    "-p:Deterministic=true", "-p:ContinuousIntegrationBuild=true", "-p:TreatWarningsAsErrors=true",
    "-o", $workerPublish
)
& dotnet @workerArgs
if ($LASTEXITCODE -ne 0) { throw "Worker publish failed." }
$workerExe = Join-Path $workerPublish "EdgeRetails.Worker.exe"
if (-not (Test-Path $workerExe -PathType Leaf)) { throw "Publish did not produce EdgeRetails.Worker.exe." }

# Publish Server
dotnet restore $ServerProject -r $Runtime
if ($LASTEXITCODE -ne 0) { throw "Server runtime restore failed." }
$serverPublish = Join-Path $publish "server"
$serverArgs = @(
    "publish", $ServerProject, "-c", "Release", "-r", $Runtime, "--self-contained", "true", "--no-restore",
    "-p:Version=$Version", "-p:FileVersion=$Version", "-p:AssemblyVersion=$Version",
    "-p:Product=Edge Retails Server", "-p:Company=Edge Retails", "-p:Description=Edge Retails LAN Server",
    "-p:PublishSingleFile=false", "-p:DebugType=None", "-p:DebugSymbols=false",
    "-p:Deterministic=true", "-p:ContinuousIntegrationBuild=true", "-p:TreatWarningsAsErrors=true",
    "-o", $serverPublish
)
& dotnet @serverArgs
if ($LASTEXITCODE -ne 0) { throw "Server publish failed." }
$serverExe = Join-Path $serverPublish "EdgeRetails.Server.exe"
if (-not (Test-Path $serverExe -PathType Leaf)) { throw "Publish did not produce EdgeRetails.Server.exe." }

# The production app performs target-machine PostgreSQL toolchain readiness at runtime. When PgBin is supplied
# to this build, validate the exact tools now as an additional release gate.
if (-not [string]::IsNullOrWhiteSpace($PgBin)) {
    & "$PSScriptRoot/Test-PostgresClientReadiness.ps1" -PgBin $PgBin
    if ($LASTEXITCODE -ne 0) { throw "PostgreSQL client readiness gate failed." }
}

& "$PSScriptRoot/Verify-Sprint8Phase1.ps1" -Root $root
& "$PSScriptRoot/Verify-Sprint8Phase2.ps1" -Root $root
& "$PSScriptRoot/Verify-Sprint8Phase3.ps1" -Root $root
& "$PSScriptRoot/Verify-InstallerDataPreservation.ps1" -Root $root
if (-not [string]::IsNullOrWhiteSpace($FingerprintPath)) {
    & "$PSScriptRoot/Verify-Sprint8Architecture.ps1" -Root $root
}

$msiProject = Join-Path $root "installer/EdgeRetails.Setup/EdgeRetails.Setup.wixproj"
dotnet build $msiProject -c Release -p:Platform=$wixPlatform -p:ProductVersion=$Version -p:PublishDir=$publish -p:AppIconPath=$iconPath -o $msiOut -warnaserror
if ($LASTEXITCODE -ne 0) { throw "WiX MSI build failed." }
$msi = Get-ChildItem $msiOut -Filter *.msi -File | Select-Object -First 1
if (-not $msi) { throw "WiX MSI build completed without producing an MSI." }

$bundleProject = Join-Path $root "installer/EdgeRetails.Bootstrapper/EdgeRetails.Bootstrapper.wixproj"
dotnet build $bundleProject -c Release -p:Platform=$wixPlatform -p:ProductVersion=$Version "-p:MsiPath=$($msi.FullName)" "-p:AppIconPath=$iconPath" -o $bundleOut -warnaserror
if ($LASTEXITCODE -ne 0) { throw "WiX Burn bundle build failed." }
$setup = Get-ChildItem $bundleOut -Filter EdgeRetailsSetup.exe -File | Select-Object -First 1
if (-not $setup) { throw "WiX Burn build completed without producing EdgeRetailsSetup.exe." }

$artifacts = @($exe, $workerExe, $serverExe, $msi.FullName, $setup.FullName)
$hashes = foreach ($file in $artifacts) {
    $h = Get-FileHash $file -Algorithm SHA256
    [pscustomobject]@{ File = (Split-Path $file -Leaf); Sha256 = $h.Hash; Bytes = (Get-Item $file).Length }
}
$canonicalPath = Join-Path $root "docs/Edge_Retails_Final_Architecture_Report_v1.md"
$actualCanonicalSha = (Get-FileHash $canonicalPath -Algorithm SHA256).Hash

$manifest = [ordered]@{
    product = "Edge Retails"
    version = $Version
    runtime = $Runtime
    installerPlatform = $wixPlatform
    selfContained = $true
    canonicalArchitectureSha256 = $actualCanonicalSha
    workspaceFingerprint = if ($fingerprint) { $fingerprint.aggregateSha256 } else { $null }
    preflightEvidenceSha256 = if ($preflightHash) { $preflightHash } else { $null }
    createdAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    artifacts = $hashes
    targetReadiness = [ordered]@{ postgresClientTools = @("pg_dump", "pg_restore", "psql", "createdb"); enforcement = "runtime diagnostics/startup plus optional release PgBin gate" }
}
$manifest | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $root "$Output/release-manifest.json") -Encoding UTF8
$hashes | ForEach-Object { "$($_.Sha256)  $($_.File)" } | Set-Content (Join-Path $root "$Output/SHA256SUMS.txt") -Encoding ASCII

Write-Host "Release publish complete: $publish"
Write-Host "MSI output: $($msi.FullName)"
Write-Host "Windows setup output: $($setup.FullName)"
Write-Host "Release manifest: $(Join-Path $root "$Output/release-manifest.json")"
