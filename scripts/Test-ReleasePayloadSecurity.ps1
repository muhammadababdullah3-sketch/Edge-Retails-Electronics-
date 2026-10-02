$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'ReleasePayloadSecurity.psm1') -Force

$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$testRoot = Join-Path $tempRoot ("EdgeRetails-PayloadScanTest-" + [Guid]::NewGuid().ToString('N'))
if (Test-Path -LiteralPath $testRoot) { throw "Unique payload test directory unexpectedly exists." }
[void](New-Item -ItemType Directory -Path $testRoot)
$ownerMarker = Join-Path $testRoot '.owned-by-payload-scan-test'
Set-Content -LiteralPath $ownerMarker -Value 'EdgeRetails payload scan test' -NoNewline
$passed = $false
try {
    $cleanPath = Join-Path $testRoot 'clean'
    [void](New-Item -ItemType Directory -Path $cleanPath)
    Set-Content -LiteralPath (Join-Path $cleanPath 'application.bin') -Value 'ordinary application payload' -NoNewline
    Assert-NoRecoveryPrivateKeyMaterial $cleanPath
    Write-Output 'PASS clean payload accepted'

    $namePath = Join-Path $testRoot 'bad-name'
    [void](New-Item -ItemType Directory -Path $namePath)
    Set-Content -LiteralPath (Join-Path $namePath 'signing-key.bin') -Value 'opaque bytes' -NoNewline
    try { Assert-NoRecoveryPrivateKeyMaterial $namePath; throw 'Expected a signing-key filename to be rejected.' }
    catch { if ($_.Exception.Message -notlike 'Release payload contains a prohibited signing-key asset name:*') { throw } }
    Write-Output 'PASS private-key asset name rejected'

    $extensionPath = Join-Path $testRoot 'bad-extension'
    [void](New-Item -ItemType Directory -Path $extensionPath)
    Set-Content -LiteralPath (Join-Path $extensionPath 'payload.der') -Value 'opaque bytes' -NoNewline
    try { Assert-NoRecoveryPrivateKeyMaterial $extensionPath; throw 'Expected a private-key extension to be rejected.' }
    catch { if ($_.Exception.Message -notlike 'Release payload contains a prohibited signing-key asset name:*') { throw } }
    Write-Output 'PASS private-key container extension rejected'

    $markerPath = Join-Path $testRoot 'bad-content'
    [void](New-Item -ItemType Directory -Path $markerPath)
    $privateKeyMarker = '-----BEGIN RSA ' + 'PRIVATE KEY-----'
    Set-Content -LiteralPath (Join-Path $markerPath 'renamed.bin') -Value $privateKeyMarker -NoNewline
    try { Assert-NoRecoveryPrivateKeyMaterial $markerPath; throw 'Expected private-key PEM content to be rejected.' }
    catch { if ($_.Exception.Message -notlike 'Release payload contains a private signing-key marker:*') { throw } }
    Write-Output 'PASS embedded private-key PEM marker rejected'

    $hiddenPath = Join-Path $testRoot 'hidden-content'
    [void](New-Item -ItemType Directory -Path $hiddenPath)
    $hiddenFile = Join-Path $hiddenPath 'hidden-payload.bin'
    Set-Content -LiteralPath $hiddenFile -Value '-----BEGIN OPENSSH PRIVATE KEY-----' -NoNewline
    $hiddenItem = Get-Item -LiteralPath $hiddenFile -Force
    $hiddenItem.Attributes = $hiddenItem.Attributes -bor [IO.FileAttributes]::Hidden
    try { Assert-NoRecoveryPrivateKeyMaterial $hiddenPath; throw 'Expected hidden private-key material to be rejected.' }
    catch { if ($_.Exception.Message -notlike 'Release payload contains a private signing-key marker:*') { throw } }
    Write-Output 'PASS hidden private-key marker rejected'
    $passed = $true
}
finally {
    $resolvedRoot = [IO.Path]::GetFullPath($testRoot)
    $resolvedTemp = [IO.Path]::GetFullPath($tempRoot).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $tempPrefix = $resolvedTemp + [IO.Path]::DirectorySeparatorChar
    if ($passed -and $resolvedRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $ownerMarker -PathType Leaf) -and
        ((Get-Item -LiteralPath $ownerMarker).Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) {
        Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
    }
    elseif (Test-Path -LiteralPath $testRoot) {
        Write-Warning "Payload scan test directory preserved for review: $testRoot"
    }
}

if (-not $passed) { throw 'Release payload security scanner did not complete all gates.' }
Write-Output 'RELEASE_PAYLOAD_SECURITY_TESTS_PASS Passed=5 Failed=0 Skipped=0'
