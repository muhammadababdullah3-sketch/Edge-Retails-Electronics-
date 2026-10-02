function ConvertFrom-EdgeRetailsNpgsqlConnectionString {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$ConnectionString)

    if ([string]::IsNullOrWhiteSpace($ConnectionString)) { throw 'CONNECTION_STRING_EMPTY' }
    $values = @{}
    $index = 0
    while ($index -lt $ConnectionString.Length) {
        while ($index -lt $ConnectionString.Length -and ($ConnectionString[$index] -eq ';' -or [char]::IsWhiteSpace($ConnectionString[$index]))) { $index++ }
        if ($index -ge $ConnectionString.Length) { break }

        $keyStart = $index
        while ($index -lt $ConnectionString.Length -and $ConnectionString[$index] -ne '=' -and $ConnectionString[$index] -ne ';') { $index++ }
        if ($index -ge $ConnectionString.Length -or $ConnectionString[$index] -ne '=') { throw 'CONNECTION_STRING_MISSING_EQUALS' }
        $key = $ConnectionString.Substring($keyStart, $index - $keyStart).Trim()
        if ([string]::IsNullOrWhiteSpace($key)) { throw 'CONNECTION_STRING_EMPTY_KEY' }
        if ($values.ContainsKey($key)) { throw 'CONNECTION_STRING_DUPLICATE_PROPERTY' }
        $index++
        while ($index -lt $ConnectionString.Length -and [char]::IsWhiteSpace($ConnectionString[$index])) { $index++ }

        $value = ''
        if ($index -lt $ConnectionString.Length -and ($ConnectionString[$index] -eq '"' -or $ConnectionString[$index] -eq "'")) {
            $quote = $ConnectionString[$index]
            $index++
            $builder = New-Object Text.StringBuilder
            $closed = $false
            while ($index -lt $ConnectionString.Length) {
                $character = $ConnectionString[$index]
                if ($character -eq $quote) {
                    if ($index + 1 -lt $ConnectionString.Length -and $ConnectionString[$index + 1] -eq $quote) {
                        [void]$builder.Append($quote)
                        $index += 2
                        continue
                    }
                    $index++
                    $closed = $true
                    break
                }
                if ($character -eq '\' -and $index + 1 -lt $ConnectionString.Length -and
                    ($ConnectionString[$index + 1] -eq $quote -or $ConnectionString[$index + 1] -eq '\')) {
                    [void]$builder.Append($ConnectionString[$index + 1])
                    $index += 2
                    continue
                }
                [void]$builder.Append($character)
                $index++
            }
            if (-not $closed) { throw 'CONNECTION_STRING_UNTERMINATED_QUOTE' }
            $value = $builder.ToString()
            while ($index -lt $ConnectionString.Length -and [char]::IsWhiteSpace($ConnectionString[$index])) { $index++ }
            if ($index -lt $ConnectionString.Length -and $ConnectionString[$index] -ne ';') { throw 'CONNECTION_STRING_TRAILING_DATA' }
        }
        else {
            $valueStart = $index
            while ($index -lt $ConnectionString.Length -and $ConnectionString[$index] -ne ';') { $index++ }
            $value = $ConnectionString.Substring($valueStart, $index - $valueStart).Trim()
        }

        $values[$key] = $value
        if ($index -lt $ConnectionString.Length -and $ConnectionString[$index] -eq ';') { $index++ }
    }
    return $values
}

function Get-EdgeRetailsConnectionStringValue {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][hashtable]$Builder,
        [Parameter(Mandatory)][string[]]$Names,
        [AllowNull()][string]$DefaultValue
    )

    $found = @()
    foreach ($name in $Names) {
        foreach ($key in $Builder.Keys) {
            if ([string]::Equals([string]$key, $name, [StringComparison]::OrdinalIgnoreCase)) {
                $found += [string]$Builder[$key]
            }
        }
    }
    $found = @($found | Select-Object -Unique)
    if ($found.Count -gt 1) { throw 'CONFIG_DUPLICATE_CONNECTION_PROPERTY' }
    if ($found.Count -eq 1) { return $found[0] }
    return $DefaultValue
}

Export-ModuleMember -Function ConvertFrom-EdgeRetailsNpgsqlConnectionString, Get-EdgeRetailsConnectionStringValue
