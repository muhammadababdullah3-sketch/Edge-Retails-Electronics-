Set-StrictMode -Version Latest

function ConvertFrom-Phase3PostgresServerAddress {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Value)

    $parts = $Value.Trim().Split('/')
    if ($parts.Count -notin @(1, 2)) { throw 'POSTGRES_SERVER_ADDRESS_INVALID' }

    $address = $null
    if (-not [Net.IPAddress]::TryParse($parts[0], [ref]$address)) { throw 'POSTGRES_SERVER_ADDRESS_INVALID' }
    if ($address.AddressFamily -eq [Net.Sockets.AddressFamily]::InterNetwork -and $parts[0] -cne $address.ToString()) {
        throw 'POSTGRES_SERVER_ADDRESS_INVALID'
    }

    $prefixLength = $null
    $hasHostPrefix = ($parts.Count -eq 1)
    if ($parts.Count -eq 2) {
        $parsedPrefix = 0
        if (-not [int]::TryParse($parts[1], [Globalization.NumberStyles]::None, [Globalization.CultureInfo]::InvariantCulture, [ref]$parsedPrefix)) {
            throw 'POSTGRES_SERVER_ADDRESS_INVALID'
        }

        $prefixLength = $parsedPrefix
        $addressBitLength = if ($address.AddressFamily -eq [Net.Sockets.AddressFamily]::InterNetwork) { 32 } else { 128 }
        $hasHostPrefix = ($parsedPrefix -eq $addressBitLength)
    }

    return [pscustomobject]@{
        Address = $address
        NormalizedAddress = $address.ToString()
        PrefixLength = $prefixLength
        HasHostPrefix = $hasHostPrefix
        IsLoopback = [Net.IPAddress]::IsLoopback($address)
    }
}

Export-ModuleMember -Function ConvertFrom-Phase3PostgresServerAddress
