param(
    [string]$Version = "1.0.0",
    [string]$InformationalVersion = "1.0.0-desktop-test",
    [string]$Runtime = "win-x64",
    [string]$DesktopProject = "src/EdgeRetails.Desktop/EdgeRetails.Desktop.csproj",
    [string]$OutputRoot = "artifacts/desktop-testing"
)
$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if ($Runtime -notmatch '^win-(x64|arm64)$') { throw "Runtime must be win-x64 or win-arm64." }
$wixPlatform = if ($Runtime -eq 'win-arm64') { 'arm64' } else { 'x64' }

$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$outDir = Join-Path $root "$OutputRoot/EdgeRetails-Desktop-Test-$stamp"
$publish = Join-Path $outDir "publish"
$msiOut = Join-Path $outDir "msi"
$setupOut = Join-Path $outDir "setup"
$zipPath = Join-Path $outDir "EdgeRetails-Desktop-Test-$stamp.zip"

if (Test-Path $outDir) { throw "Output path already exists: $outDir" }
New-Item -ItemType Directory -Path $publish, $msiOut, $setupOut | Out-Null

Write-Host "Building Release desktop ($Runtime)..."
dotnet build $DesktopProject -c Release -warnaserror
if ($LASTEXITCODE -ne 0) { throw "Desktop build failed." }

[xml]$desktopXml = Get-Content (Join-Path $root $DesktopProject)
$iconValue = @(
    $desktopXml.Project.PropertyGroup |
        ForEach-Object { [string]$_.ApplicationIcon } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
) | Select-Object -First 1
$iconPath = $null
if ($iconValue) { $iconPath = Join-Path (Split-Path (Join-Path $root $DesktopProject) -Parent) $iconValue }
if ($iconPath -and (Test-Path $iconPath -PathType Leaf)) { $iconPath = (Resolve-Path $iconPath).Path }
if (-not $iconPath) { throw "Desktop ApplicationIcon is required for packaging." }

$publishArgs = @(
    "publish", (Join-Path $root $DesktopProject), "-c", "Release", "-r", $Runtime,
    "--self-contained", "true",
    "-p:Version=$Version", "-p:FileVersion=$Version", "-p:InformationalVersion=$InformationalVersion",
    "-p:PublishSingleFile=false", "-p:DebugType=None", "-p:DebugSymbols=false",
    "-p:TreatWarningsAsErrors=true",
    "-o", $publish
)
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "Desktop publish failed." }

$exe = Join-Path $publish "EdgeRetails.Desktop.exe"
if (-not (Test-Path $exe)) { throw "EdgeRetails.Desktop.exe missing after publish." }

$readme = @"
# Edge Retails Desktop — testing pack (no bundled Shop Server)

Built: $stamp
Version: $Version ($InformationalVersion)

## What this is
- Self-contained **Desktop POS client only** (WPF).
- **Does not** include EdgeRetails.Server, Worker, or PostgreSQL.
- Use for UI/workflow testing against a Shop Server you run separately.

## Run
1. Ensure a Shop Server is reachable (default: http://127.0.0.1:7150).
2. Optional: set server URL before launch:
   ``set EDGE_RETAILS_SERVER_URL=http://127.0.0.1:7150``
3. Run ``EdgeRetails.Desktop.exe`` from the ``publish`` folder (or after MSI install).

## First-time shop
- Complete **First Setup** + **Login** when the server reports setup complete.
- **Open cash drawer** prompt appears after login when no session is open (POS cash sales need an open session).

## Debug preview (no server)
- ``EdgeRetails.Desktop.exe --sprint1-preview`` (DEBUG builds only; not in this Release pack).

## Installer (if present)
- ``setup/EdgeRetailsSetup.exe`` — per-machine install to Program Files\Edge Retails
- Portable copy: ``publish/`` folder

## Next pack
When Server + Worker are integrated, rebuild with the full ``Publish-Release.ps1`` pipeline.
"@
Set-Content -Path (Join-Path $outDir "README-TESTING.txt") -Value $readme -Encoding UTF8

$manifest = [ordered]@{
    packKind = "desktop-testing"
    builtUtc = [DateTimeOffset]::UtcNow.ToString("o")
    version = $Version
    informationalVersion = $InformationalVersion
    runtime = $Runtime
    desktopExeSha256 = (Get-FileHash $exe -Algorithm SHA256).Hash
    integrationEvidence = "ER-RUN-POS-FULL-VERIFY-20261026-081028"
    note = "Server binaries intentionally omitted for this test pack."
}
$manifest | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $outDir "pack-manifest.json") -Encoding UTF8

$wixBuilt = $false
try {
    $msiProject = Join-Path $root "installer/EdgeRetails.Setup/EdgeRetails.Setup.wixproj"
    if (Test-Path $msiProject) {
        Write-Host "Building WiX MSI (desktop payload only)..."
        dotnet build $msiProject -c Release -p:Platform=$wixPlatform `
            -p:ProductVersion=1.0.0 -p:PublishDir=$publish -p:AppIconPath=$iconPath -o $msiOut
        if ($LASTEXITCODE -eq 0) {
            $msi = Get-ChildItem $msiOut -Filter *.msi -File | Select-Object -First 1
            if ($msi) {
                $bundleProject = Join-Path $root "installer/EdgeRetails.Bootstrapper/EdgeRetails.Bootstrapper.wixproj"
                dotnet build $bundleProject -c Release -p:Platform=$wixPlatform `
                    -p:ProductVersion=1.0.0 "-p:MsiPath=$($msi.FullName)" "-p:AppIconPath=$iconPath" -o $setupOut
                if ($LASTEXITCODE -eq 0) {
                    $wixBuilt = $true
                }
            }
        }
    }
}
catch {
    Write-Warning "WiX installer skipped: $($_.Exception.Message)"
}

Write-Host "Creating ZIP..."
Compress-Archive -Path (Join-Path $publish "*") -DestinationPath $zipPath -Force

Write-Host ""
Write-Host "Desktop testing pack ready:"
Write-Host "  $outDir"
Write-Host "  ZIP: $zipPath"
if ($wixBuilt) {
    Write-Host "  Setup: $(Join-Path $setupOut 'EdgeRetailsSetup.exe')"
} else {
    Write-Host "  Setup: (not built - use publish folder or install WiX SDK)"
}
