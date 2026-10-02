if (-not ('EdgeRetailsReleaseKeyScanner' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
public static class EdgeRetailsReleaseKeyScanner {
    public static bool Contains(byte[] data, byte[] pattern) {
        if (data == null || pattern == null || pattern.Length == 0 || data.Length < pattern.Length) return false;
        int offset = 0;
        int lastStart = data.Length - pattern.Length;
        while (offset <= lastStart) {
            int candidate = Array.IndexOf(data, pattern[0], offset);
            if (candidate < 0 || candidate > lastStart) return false;
            int index = 1;
            while (index < pattern.Length && data[candidate + index] == pattern[index]) index++;
            if (index == pattern.Length) return true;
            offset = candidate + 1;
        }
        return false;
    }
}
'@ -ErrorAction Stop
}

function Assert-NoRecoveryPrivateKeyMaterial([string]$Path) {
    $forbiddenExtensions = @('.pfx', '.p12', '.pem', '.key', '.keystore', '.jks', '.snk', '.der', '.p8', '.pkcs8', '.priv', '.pvk', '.p7b', '.p7c', '.asc', '.ppk', '.ssh')
    $forbiddenNames = '(?i)(private|secret|signing)[._ -]?(key|credential)'
    $markers = @(
        [Text.Encoding]::ASCII.GetBytes('-----BEGIN PRIVATE KEY-----'),
        [Text.Encoding]::ASCII.GetBytes('-----BEGIN RSA PRIVATE KEY-----'),
        [Text.Encoding]::ASCII.GetBytes('-----BEGIN DSA PRIVATE KEY-----'),
        [Text.Encoding]::ASCII.GetBytes('-----BEGIN EC PRIVATE KEY-----'),
        [Text.Encoding]::ASCII.GetBytes('-----BEGIN ENCRYPTED PRIVATE KEY-----'),
        [Text.Encoding]::ASCII.GetBytes('-----BEGIN OPENSSH PRIVATE KEY-----'),
        [Text.Encoding]::ASCII.GetBytes('-----BEGIN SSH2 ENCRYPTED PRIVATE KEY-----')
    )
    $rootItem = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if (($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Release payload root is a reparse point: $Path"
    }
    $entries = @(Get-ChildItem -LiteralPath $Path -Recurse -Force -ErrorAction Stop)
    foreach ($entry in $entries) {
        if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Release payload contains a reparse point that prevents complete inspection: $($entry.FullName)"
        }
    }
    foreach ($file in $entries | Where-Object { -not $_.PSIsContainer }) {
        if ($forbiddenExtensions -contains $file.Extension.ToLowerInvariant() -or $file.Name -match $forbiddenNames) {
            throw "Release payload contains a prohibited signing-key asset name: $($file.FullName)"
        }
        $bytes = [IO.File]::ReadAllBytes($file.FullName)
        foreach ($marker in $markers) {
            if ([EdgeRetailsReleaseKeyScanner]::Contains($bytes, $marker)) {
                throw "Release payload contains a private signing-key marker: $($file.FullName)"
            }
        }
    }
}

function Assert-InstallerPayloadContainsNoRecoveryPrivateKey([string]$WixCliPath, [string]$BundlePath, [string]$MsiPath) {
    if (-not (Test-Path -LiteralPath $WixCliPath -PathType Leaf)) {
        throw "WiX CLI was not found for static Burn extraction: $WixCliPath"
    }
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    $auditRoot = Join-Path $tempRoot ("EdgeRetails-ReleaseAudit-" + [Guid]::NewGuid().ToString('N'))
    if (Test-Path -LiteralPath $auditRoot) { throw "Unique release audit directory unexpectedly exists: $auditRoot" }
    [void](New-Item -ItemType Directory -Path $auditRoot)
    $ownerMarker = Join-Path $auditRoot '.owned-by-publish-release'
    Set-Content -LiteralPath $ownerMarker -Value 'EdgeRetails release payload audit' -NoNewline
    $passed = $false
    try {
        $bundleLayout = Join-Path $auditRoot 'bundle-layout'
        $bootstrapperApplicationLayout = Join-Path $auditRoot 'bootstrapper-application-layout'
        $burnIntermediate = Join-Path $auditRoot 'burn-intermediate'
        [void](New-Item -ItemType Directory -Path $bundleLayout,$bootstrapperApplicationLayout,$burnIntermediate)
        # Use WiX's static extractor so security inspection never launches the candidate installer.
        $extractOutput = @(& dotnet $WixCliPath burn extract -intermediateFolder $burnIntermediate $BundlePath -o $bundleLayout -outba $bootstrapperApplicationLayout 2>&1)
        $extractExitCode = $LASTEXITCODE
        $extractOutput | ForEach-Object { Write-Output $_ }
        if ($extractExitCode -ne 0) { throw "Static Burn extraction failed with exit code $extractExitCode." }
        Assert-NoRecoveryPrivateKeyMaterial $bundleLayout
        Assert-NoRecoveryPrivateKeyMaterial $bootstrapperApplicationLayout
        if (@(Get-ChildItem -LiteralPath $bundleLayout -Recurse -Force -File).Count -eq 0) { throw 'Static Burn extraction produced an empty bundle payload tree.' }
        if (@(Get-ChildItem -LiteralPath $bootstrapperApplicationLayout -Recurse -Force -File).Count -eq 0) { throw 'Static Burn extraction produced an empty bootstrapper-application payload tree.' }

        $expectedMsiHash = (Get-FileHash -LiteralPath $MsiPath -Algorithm SHA256).Hash
        $matchingMsiPayloads = @(
            Get-ChildItem -LiteralPath $bundleLayout -Recurse -File |
                Where-Object { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash -eq $expectedMsiHash }
        )
        if ($matchingMsiPayloads.Count -ne 1) { throw "Expected exactly one extracted Burn payload matching the standalone MSI hash; found $($matchingMsiPayloads.Count)." }
        $laidOutMsiPath = Join-Path $bundleLayout 'EdgeRetailsSetup-extracted.msi'
        if (-not [string]::Equals($matchingMsiPayloads[0].FullName, $laidOutMsiPath, [StringComparison]::OrdinalIgnoreCase)) {
            Copy-Item -LiteralPath $matchingMsiPayloads[0].FullName -Destination $laidOutMsiPath
        }
        $laidOutMsi = Get-Item -LiteralPath $laidOutMsiPath
        $bundledMsiHash = (Get-FileHash -LiteralPath $laidOutMsi.FullName -Algorithm SHA256).Hash
        if ($expectedMsiHash -ne $bundledMsiHash) { throw "The MSI extracted from Burn does not match the separately manifested MSI." }

        $administrativeImage = Join-Path $auditRoot 'msi-administrative-image'
        [void](New-Item -ItemType Directory -Path $administrativeImage)
        $msiArgs = @('/a', "`"$($laidOutMsi.FullName)`"", '/qn', '/norestart', "TARGETDIR=`"$administrativeImage`"")
        $extractProcess = Start-Process -FilePath "$env:SystemRoot\System32\msiexec.exe" -ArgumentList $msiArgs -Wait -PassThru -WindowStyle Hidden
        if ($extractProcess.ExitCode -ne 0) { throw "MSI administrative payload extraction failed with exit code $($extractProcess.ExitCode)." }
        Assert-NoRecoveryPrivateKeyMaterial $administrativeImage

        foreach ($requiredFile in @('EdgeRetails.Desktop.exe', 'EdgeRetails.Server.exe', 'EdgeRetails.Worker.exe', 'EdgeRetails.Recovery.exe')) {
            if (@(Get-ChildItem -LiteralPath $administrativeImage -Recurse -File -Filter $requiredFile).Count -ne 1) {
                throw "Extracted installer payload does not contain exactly one $requiredFile."
            }
        }
        $passed = $true
    }
    finally {
        $resolvedRoot = [IO.Path]::GetFullPath($auditRoot)
        $resolvedTemp = [IO.Path]::GetFullPath($tempRoot).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
        $tempPrefix = $resolvedTemp + [IO.Path]::DirectorySeparatorChar
        if ($passed -and $resolvedRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -and
            (Test-Path -LiteralPath $ownerMarker -PathType Leaf) -and
            ((Get-Item -LiteralPath $ownerMarker).Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) {
            Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
        }
        elseif (Test-Path -LiteralPath $auditRoot) {
            Write-Warning "Release payload audit directory preserved for review: $auditRoot"
        }
    }
}

Export-ModuleMember -Function Assert-NoRecoveryPrivateKeyMaterial, Assert-InstallerPayloadContainsNoRecoveryPrivateKey
