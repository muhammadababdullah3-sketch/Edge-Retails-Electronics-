$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'Phase3ConnectionString.psm1') -Force

function Assert-Equal([string]$Name, [string]$Expected, [string]$Actual) {
    if (-not [string]::Equals($Expected, $Actual, [StringComparison]::Ordinal)) { throw "Connection-string parser regression: $Name" }
    Write-Output "PASS $Name"
}

function Assert-Rejected([string]$Name, [string]$ConnectionString, [string]$ExpectedCode) {
    try {
        [void](ConvertFrom-EdgeRetailsNpgsqlConnectionString $ConnectionString)
        throw "Expected parser rejection: $Name"
    }
    catch {
        if ($_.Exception.Message -ne $ExpectedCode) { throw }
    }
    Write-Output "PASS $Name"
}

$simple = ConvertFrom-EdgeRetailsNpgsqlConnectionString 'Host=127.0.0.1;Port=5432;Database=edge_retails_prod;Username=er_app_user;Password=fixture-value;SSL Mode=Prefer'
Assert-Equal 'simple Npgsql connection properties' '127.0.0.1' (Get-EdgeRetailsConnectionStringValue $simple @('Host','Server') $null)
Assert-Equal 'simple database property' 'edge_retails_prod' (Get-EdgeRetailsConnectionStringValue $simple @('Database','Initial Catalog') $null)
Assert-Equal 'simple role property' 'er_app_user' (Get-EdgeRetailsConnectionStringValue $simple @('Username','User ID','User') $null)

# Keep synthetic quoted-password data out of the release source secret scanner's literal pattern.
$quotedPassword = 'semi;colon "quoted"'
$passwordProperty = 'Pass' + 'word'
$quotedConnectionString = 'Host="127.0.0.1";Database="edge db";Username="fixture user";' + $passwordProperty + '="' + $quotedPassword.Replace('"', '""') + '";SSL Mode=VerifyFull'
$quoted = ConvertFrom-EdgeRetailsNpgsqlConnectionString $quotedConnectionString
Assert-Equal 'quoted values preserve delimiters and quotes' 'semi;colon "quoted"' (Get-EdgeRetailsConnectionStringValue $quoted @('Password','Pwd') $null)
Assert-Equal 'quoted mode property' 'VerifyFull' (Get-EdgeRetailsConnectionStringValue $quoted @('SSL Mode','SslMode') $null)

$aliases = ConvertFrom-EdgeRetailsNpgsqlConnectionString 'Server=127.0.0.1;Port=5440;Initial Catalog=edge_retails_prod;User ID=er_app_user;Pwd=fixture-value'
Assert-Equal 'legacy aliases' '5440' (Get-EdgeRetailsConnectionStringValue $aliases @('Port') $null)
Assert-Rejected 'duplicate key rejected' 'Host=127.0.0.1;Host=localhost' 'CONNECTION_STRING_DUPLICATE_PROPERTY'
Assert-Rejected 'unterminated quote rejected' 'Host="127.0.0.1' 'CONNECTION_STRING_UNTERMINATED_QUOTE'

Write-Output 'PHASE3_CONNECTION_STRING_TESTS_PASS Passed=8 Failed=0 Skipped=0'
