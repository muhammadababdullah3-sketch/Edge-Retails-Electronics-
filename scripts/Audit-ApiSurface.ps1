param(
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path,
    [string]$OutputPath = (Join-Path $RepoRoot "governance\api-surface\ORPHAN_REGISTRY.md")
)

$ErrorActionPreference = "Stop"
$controllersRoot = Join-Path $RepoRoot "src\EdgeRetails.Server\Controllers"
$consumerRoots = @(
    (Join-Path $RepoRoot "src\EdgeRetails.Desktop"),
    (Join-Path $RepoRoot "src\EdgeRetails.Application\Gateways\RemoteApplicationGateway.cs"),
    (Join-Path $RepoRoot "src\EdgeRetails.Worker")
)

$routes = @()
Get-ChildItem -Path $controllersRoot -Filter "*Controller.cs" -Recurse | ForEach-Object {
    $controllerFile = $_.FullName
    $text = Get-Content $controllerFile -Raw
    $classRoute = [regex]::Match($text, '\[Route\("([^"]+)"\)\]').Groups[1].Value
    if ([string]::IsNullOrWhiteSpace($classRoute)) { $classRoute = "api/[controller]" }
    $classRoute = $classRoute -replace '\[controller\]', $_.BaseName.Replace("Controller", "").ToLowerInvariant()

    [regex]::Matches($text, '\[Http(?<verb>\w+)\("(?<template>[^"]*)"\)\]') | ForEach-Object {
        $verb = $_.Groups["verb"].Value.ToUpperInvariant()
        $template = $_.Groups["template"].Value
        $path = "/$classRoute/$template".Replace("//", "/").TrimEnd("/")
        if ($path -eq "/$classRoute") { $path = "/$classRoute" }
        $routes += [pscustomobject]@{ Verb = $verb; Path = $path; Source = $controllerFile }
    }
}

$consumerText = ($consumerRoots | ForEach-Object {
    if (Test-Path $_ -PathType Container) {
        Get-ChildItem $_ -Recurse -Include *.cs,*.xaml -File | ForEach-Object { Get-Content $_.FullName -Raw }
    } elseif (Test-Path $_) {
        Get-Content $_ -Raw
    }
}) -join "`n"

$orphans = foreach ($route in $routes | Sort-Object Path, Verb -Unique) {
    $needle = $route.Path
    if ($consumerText -notmatch [regex]::Escape($needle)) {
        $route
    }
}

$dir = Split-Path $OutputPath -Parent
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }

$lines = @(
    "# API surface orphan registry",
    "",
    "Generated: $(Get-Date -Format o)",
    "",
    "Routes declared on Server controllers without a matching path string in Desktop, RemoteApplicationGateway, or Worker.",
    "",
    "| Verb | Path | Controller |",
    "|------|------|------------|"
)

foreach ($orphan in $orphans) {
    $lines += "| $($orphan.Verb) | $($orphan.Path) | $(Split-Path $orphan.Source -Leaf) |"
}

if ($orphans.Count -eq 0) {
    $lines += "| - | _(none detected by path grep)_ | - |"
}

Set-Content -Path $OutputPath -Value ($lines -join [Environment]::NewLine) -Encoding UTF8
Write-Host "Wrote $($orphans.Count) orphan route(s) to $OutputPath"
