param(
    [Parameter(Mandatory=$true)][string]$OutputPath,
    [Parameter(Mandatory=$true)][ValidatePattern('\Acentral[a-z0-9_]+\z')][string]$SourceDatabase,
    [Parameter(Mandatory=$true)][ValidateRange(1024,65535)][int]$Port,
    [Parameter(Mandatory=$true)][ValidateRange(1,2147483647)][int]$PostgresPid,
    [Parameter(Mandatory=$true)][string]$PostgresBinary,
    [Parameter(Mandatory=$true)][string]$DataDirectory
)

$ErrorActionPreference='Stop'
$rootsValue=[Environment]::GetEnvironmentVariable('CENTRAL_RECOVERY_OWNED_ROOTS')
if([string]::IsNullOrWhiteSpace($rootsValue)){$rootsValue=[Environment]::GetEnvironmentVariable('CENTRAL_RECOVERY_OWNED_ROOT')}
if([string]::IsNullOrWhiteSpace($rootsValue)){throw 'Set an explicit Central recovery owned root before capturing quiescence evidence.'}
$roots=@($rootsValue -split ';'|ForEach-Object { [IO.Path]::GetFullPath($_.Trim()).TrimEnd('\','/') })
$out=[IO.Path]::GetFullPath($OutputPath)
if(-not [IO.Path]::IsPathFullyQualified($OutputPath) -or -not @($roots|Where-Object { $out.StartsWith($_+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) }).Count){throw 'Quiescence output must be contained by an explicitly owned Central root.'}
foreach($path in @($out,(Split-Path -Parent $out))) {
    $cursor=$path
    while($cursor) {
        if(Test-Path -LiteralPath $cursor) { if(([IO.File]::GetAttributes($cursor) -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw 'Quiescence evidence cannot traverse links or reparse components.'} }
        $parent=Split-Path -Parent $cursor
        if(-not $parent -or $parent -eq $cursor){break}
        $cursor=$parent
    }
}
$identity=[Security.Principal.WindowsIdentity]::GetCurrent().Name
$captured=[DateTimeOffset]::UtcNow
$postgres=Get-CimInstance -ClassName Win32_Process -Filter "ProcessId=$PostgresPid" -ErrorAction Stop
$binary=[IO.Path]::GetFullPath($PostgresBinary); $data=[IO.Path]::GetFullPath($DataDirectory)
$postgresCommand=([string]$postgres.CommandLine).Replace('/','\')
if(-not $postgres -or [string]$postgres.ExecutablePath -ne $binary -or $postgresCommand -notmatch [regex]::Escape($data) -or $postgresCommand -notmatch "(?i)(?:^|\s)-p\s+$Port(?:\s|$)"){throw 'PostgreSQL process does not match the supplied binary, data directory and port.'}
$postgresLauncherPid=0
$postgresParent=Get-CimInstance -ClassName Win32_Process -Filter "ProcessId=$([int]$postgres.ParentProcessId)" -ErrorAction SilentlyContinue
if($postgresParent) {
    $parentCommand=([string]$postgresParent.CommandLine).Replace('/','\')
    if($postgresParent.Name -ne 'cmd.exe' -or $parentCommand.IndexOf($binary,[StringComparison]::OrdinalIgnoreCase) -lt 0 -or $parentCommand.IndexOf($data,[StringComparison]::OrdinalIgnoreCase) -lt 0 -or $parentCommand -notmatch "(?i)(?:^|\s)-p\s+$Port(?:\s|$)"){throw 'PostgreSQL launcher is not an attested non-writer parent process.'}
    $postgresLauncherPid=[int]$postgresParent.ProcessId
}
$processes=@()
foreach($row in @(Get-CimInstance -ClassName Win32_Process -ErrorAction Stop)) {
    $cmd=[string]$row.CommandLine; $exe=[string]$row.ExecutablePath; $processId=[int]$row.ProcessId
    $matchesRoot=$false
    foreach($root in $roots) { $needle=$root.TrimEnd('\','/'); if(($cmd -and $cmd.IndexOf($needle,[StringComparison]::OrdinalIgnoreCase) -ge 0) -or ($exe -and $exe.StartsWith($needle+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase))){$matchesRoot=$true;break} }
    $centralMarker=($cmd -match '(?i)EdgeRetails\.Central\.(Api|Worker|IntegrationTests|UnitTests)|EdgeRetails\.ProtectedSigner|Invoke-CentralDisposableRecoveryRehearsal')
    $processes+=@{processId=$processId;parentProcessId=[int]$row.ParentProcessId;name=[string]$row.Name;executablePath=$exe;commandLineSha256=[Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($cmd)));matchedOwnedRoot=$matchesRoot;matchedCentralMarker=$centralMarker}
}
$writers=@($processes|Where-Object { $_.processId -notin (@($PID,$PostgresPid,$postgresLauncherPid) | Where-Object { [int]$_ -gt 0 }) -and ($_.matchedOwnedRoot -or $_.matchedCentralMarker) })
$rawPath=[IO.Path]::ChangeExtension($out,'.processes.json')
$inventory=@{kind='windows-process-inventory';capturedAtUtc=$captured.ToString('O');coordinator=$identity;coordinatorPid=$PID;postgresPid=$PostgresPid;postgresLauncherPid=$postgresLauncherPid;processes=$processes}
$inventory|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $rawPath -Encoding utf8
$sha=(Get-FileHash -LiteralPath $rawPath -Algorithm SHA256).Hash.ToLowerInvariant()
if($writers.Count -ne 0){throw ('Owned Central writer processes remain active: '+(($writers|ForEach-Object { $_.processId }) -join ','))}
$proof=@{sourceDatabase=$SourceDatabase;port=$Port;postgresPid=$PostgresPid;postgresLauncherPid=$postgresLauncherPid;coordinator=$identity;coordinatorPid=$PID;observedAtUtc=$captured.ToString('O');fileWritersQuiesced=$true;databaseWritersQuiesced=$true;activeOwnedWriterProcessIds=@();observationEvidence=@(@{kind='windows-process-inventory';path=$rawPath;sha256=$sha})}
$proof|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $out -Encoding utf8
Write-Output ('Quiescence proof written: '+$out)
Write-Output ('Native process evidence SHA-256: '+$sha)
Write-Output ('Process inventory rows: '+$processes.Count)
