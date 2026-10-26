# Applies the last installer page: desktop shortcut, sign-in startup, and boot auto-start.
# Does not create or delete the private shop database. Auto-start is stored for the
# database and shop services, and applied immediately when those services already exist.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $InstallFolder,
    [string] $DesktopShortcut = "1",
    [string] $LaunchOnLogin = "1",
    [string] $AutoStartWithWindows = "1",
    [switch] $Remove
)

$ErrorActionPreference = "Stop"

function Test-OptionOn([string] $Value) {
    return $Value -match '^(1|true|yes)$'
}

function Get-ShortcutPath([Environment+SpecialFolder] $Folder, [string] $Name) {
    return Join-Path ([Environment]::GetFolderPath($Folder)) $Name
}

function Set-EdgeRetailsShortcut([string] $Path, [string] $Target) {
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut($Path)
    $link.TargetPath = $Target
    $link.WorkingDirectory = $InstallFolder
    $link.Description = "Edge Retails"
    $link.Save()
}

$desktopLink = Get-ShortcutPath CommonDesktopDirectory "Edge Retails.lnk"
$startupLink = Get-ShortcutPath CommonStartup "Edge Retails.lnk"
$optionsDirectory = Join-Path $env:ProgramData "EdgeRetails"
$optionsPath = Join-Path $optionsDirectory "install-options.json"
$serviceNames = @("EdgeRetailsDatabase", "EdgeRetailsServer", "EdgeRetailsWorker")

if ($Remove) {
    foreach ($path in @($desktopLink, $startupLink)) {
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
    }
    return
}

$desktopExe = Join-Path $InstallFolder "EdgeRetails.Desktop.exe"
if (-not (Test-Path -LiteralPath $desktopExe -PathType Leaf)) {
    throw "Edge Retails desktop app was not found at $desktopExe."
}

$desktopOn = Test-OptionOn $DesktopShortcut
$loginOn = Test-OptionOn $LaunchOnLogin
$bootOn = Test-OptionOn $AutoStartWithWindows

if ($desktopOn) { Set-EdgeRetailsShortcut $desktopLink $desktopExe }
elseif (Test-Path -LiteralPath $desktopLink) { Remove-Item -LiteralPath $desktopLink -Force }

if ($loginOn) { Set-EdgeRetailsShortcut $startupLink $desktopExe }
elseif (Test-Path -LiteralPath $startupLink) { Remove-Item -LiteralPath $startupLink -Force }

if (-not (Test-Path -LiteralPath $optionsDirectory)) {
    New-Item -ItemType Directory -Path $optionsDirectory | Out-Null
}

@{
    desktopShortcut = $desktopOn
    launchOnLogin = $loginOn
    autoStartWithWindows = $bootOn
} | ConvertTo-Json | Set-Content -LiteralPath $optionsPath -Encoding UTF8

$startupType = if ($bootOn) { "Automatic" } else { "Manual" }
foreach ($name in $serviceNames) {
    $service = Get-Service -Name $name -ErrorAction SilentlyContinue
    if ($service) { Set-Service -Name $name -StartupType $startupType }
}
