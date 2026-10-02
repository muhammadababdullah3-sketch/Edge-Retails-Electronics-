Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Phase3PostgresAddress.psm1') -Force

$passed = 0
$failed = 0

function Assert-AddressResult {
    param([string]$Case, [string]$Value, [bool]$ExpectedLoopback, [bool]$ExpectedHostPrefix)
    $result = ConvertFrom-Phase3PostgresServerAddress $Value
    if ($result.IsLoopback -ne $ExpectedLoopback -or $result.HasHostPrefix -ne $ExpectedHostPrefix) {
        throw "${Case}: unexpected parse result for '$Value'."
    }
    $script:passed++
    Write-Output "PASS $Case"
}

function Assert-AddressRejected {
    param([string]$Case, [string]$Value)
    try {
        $null = ConvertFrom-Phase3PostgresServerAddress $Value
        throw "${Case}: expected rejection for '$Value'."
    }
    catch {
        if ($_.Exception.Message -like "${Case}:*") { throw }
        $script:passed++
        Write-Output "PASS $Case"
    }
}

try {
    Assert-AddressResult 'IPv4 loopback host CIDR' '127.0.0.1/32' $true $true
    Assert-AddressResult 'IPv4 bare loopback' '127.0.0.1' $true $true
    Assert-AddressResult 'IPv6 loopback host CIDR' '::1/128' $true $true
    Assert-AddressResult 'Non-loopback full host CIDR' '192.0.2.10/32' $false $true
    Assert-AddressResult 'Loopback wider network CIDR' '127.0.0.1/24' $true $false
    Assert-AddressRejected 'Malformed address' '127.0.0.1/nope'
    Assert-AddressRejected 'Multiple CIDR separators' '127.0.0.1/32/32'
    Assert-AddressRejected 'Malformed IP' '127.0.0'
}
catch {
    $failed++
    Write-Error $_
}

Write-Output "TOTAL Passed=$passed Failed=$failed"
if ($failed -ne 0 -or $passed -ne 8) { exit 1 }
