param([string]$Root = ".")
$ErrorActionPreference = "Stop"
$installer = Join-Path $Root "installer"
if (-not (Test-Path $installer)) { throw "Installer sources not found." }
$files = Get-ChildItem $installer -Recurse -File -Include *.wxs,*.wixproj
$text = ($files | Get-Content -Raw) -join "`n"
$forbidden = @(
    'PostgreSQL\\data', 'ProgramData.*Remove', 'backup.*Remove', 'license.*Remove',
    'RemoveFile[^>]+Name="\*"', 'CustomAction[^>]+DROP DATABASE', 'CustomAction[^>]+Remove-Item'
)
foreach ($pattern in $forbidden) {
    if ($text -match $pattern) { throw "Installer preservation audit failed on forbidden pattern: $pattern" }
}
if ($text -notmatch 'ProgramFiles64Folder') { throw "Installer is not constrained to Program Files for application binaries." }
Write-Host "Installer data-preservation static audit PASS"
