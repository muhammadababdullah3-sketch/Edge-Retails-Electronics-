param(
    [Parameter(Mandatory=$true)][string]$OutputPath,
    [Parameter(Mandatory=$true)][ValidatePattern('\Acentral[a-z0-9_]+\z')][string]$SourceDatabase,
    [Parameter(Mandatory=$true)][ValidateRange(1024,65535)][int]$Port,
    [Parameter(Mandatory=$true)][ValidateRange(1,2147483647)][int]$PostgresPid,
    [Parameter(Mandatory=$true)][string]$PostgresBinary,
    [Parameter(Mandatory=$true)][string]$DataDirectory,
    [int]$TargetPostgresPid=0,
    [int]$TargetPort=0,
    [string]$TargetPostgresBinary,
    [string]$TargetDataDirectory
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
$targetLauncherPid=0
$targetIdentity=$null
if($TargetPostgresPid -or $TargetPort -or $TargetPostgresBinary -or $TargetDataDirectory) {
    if($TargetPostgresPid -le 0 -or $TargetPort -lt 1024 -or $TargetPort -gt 65535 -or [string]::IsNullOrWhiteSpace($TargetPostgresBinary) -or [string]::IsNullOrWhiteSpace($TargetDataDirectory) -or $TargetPostgresPid -eq $PostgresPid -or $TargetPort -eq $Port){throw 'Separate target process, port, binary and data directory must be supplied as one distinct attested instance.'}
    $target=Get-CimInstance -ClassName Win32_Process -Filter "ProcessId=$TargetPostgresPid" -ErrorAction Stop
    $targetBinary=[IO.Path]::GetFullPath($TargetPostgresBinary); $targetData=[IO.Path]::GetFullPath($TargetDataDirectory); $targetCommand=([string]$target.CommandLine).Replace('/','\')
    if([string]$target.ExecutablePath -ne $targetBinary -or $targetCommand -notmatch [regex]::Escape($targetData) -or $targetCommand -notmatch "(?i)(?:^|\s)-p\s+$TargetPort(?:\s|$)" -or [Diagnostics.FileVersionInfo]::GetVersionInfo($targetBinary).FileMajorPart -ne 18){throw 'Target PostgreSQL process does not match its binary, PostgreSQL 18 version, data directory and port.'}
    $targetPostmaster=Get-Content -LiteralPath (Join-Path $targetData 'postmaster.pid')
    if($targetPostmaster.Count -lt 4 -or [int]$targetPostmaster[0] -ne $TargetPostgresPid -or [IO.Path]::GetFullPath($targetPostmaster[1]) -ne $targetData -or [int]$targetPostmaster[3] -ne $TargetPort){throw 'Separate target postmaster identity does not match the supplied process, data directory and port.'}
    $targetListeners=@(Get-NetTCPConnection -LocalPort $TargetPort -State Listen -ErrorAction Stop)
    if($targetListeners.Count -eq 0 -or @($targetListeners|Where-Object {$_.OwningProcess -ne $TargetPostgresPid -or $_.LocalAddress -notin @('127.0.0.1','::1')}).Count -gt 0){throw 'Separate target listener is not owned exclusively by the attested loopback PostgreSQL process.'}
    $targetParent=Get-CimInstance -ClassName Win32_Process -Filter "ProcessId=$([int]$target.ParentProcessId)" -ErrorAction SilentlyContinue
    if($targetParent){$parentCommand=([string]$targetParent.CommandLine).Replace('/','\');if($targetParent.Name -ne 'cmd.exe' -or $parentCommand.IndexOf($targetBinary,[StringComparison]::OrdinalIgnoreCase) -lt 0 -or $parentCommand.IndexOf($targetData,[StringComparison]::OrdinalIgnoreCase) -lt 0 -or $parentCommand -notmatch "(?i)(?:^|\s)-p\s+$TargetPort(?:\s|$)"){throw 'Target PostgreSQL launcher is not an attested non-writer parent process.'};$targetLauncherPid=[int]$targetParent.ProcessId}
    $targetIdentity=@{processId=$TargetPostgresPid;port=$TargetPort;binary=$targetBinary;dataDirectory=$targetData;launcherPid=$targetLauncherPid}
}
$processes=@()
foreach($row in @(Get-CimInstance -ClassName Win32_Process -ErrorAction Stop)) {
    $cmd=[string]$row.CommandLine; $exe=[string]$row.ExecutablePath; $processId=[int]$row.ProcessId
    $matchesRoot=$false
    foreach($root in $roots) { $needle=$root.TrimEnd('\','/'); if(($cmd -and $cmd.IndexOf($needle,[StringComparison]::OrdinalIgnoreCase) -ge 0) -or ($exe -and $exe.StartsWith($needle+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase))){$matchesRoot=$true;break} }
    $centralMarker=($cmd -match '(?i)EdgeRetails\.Central\.(Api|Worker|IntegrationTests|UnitTests)|EdgeRetails\.ProtectedSigner|Invoke-CentralDisposableRecoveryRehearsal')
    $processes+=@{processId=$processId;parentProcessId=[int]$row.ParentProcessId;name=[string]$row.Name;executablePath=$exe;commandLineSha256=[Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($cmd)));matchedOwnedRoot=$matchesRoot;matchedCentralMarker=$centralMarker}
}
$infrastructurePids=@($PID,$PostgresPid,$postgresLauncherPid,$TargetPostgresPid,$targetLauncherPid)|Where-Object {[int]$_ -gt 0}
$writers=@($processes|Where-Object { $_.processId -notin $infrastructurePids -and ($_.matchedOwnedRoot -or $_.matchedCentralMarker) })
$rawPath=[IO.Path]::ChangeExtension($out,'.processes.json')
$inventory=@{kind='windows-process-inventory';capturedAtUtc=$captured.ToString('O');coordinator=$identity;coordinatorPid=$PID;postgresPid=$PostgresPid;postgresLauncherPid=$postgresLauncherPid;targetPostgres=$targetIdentity;processes=$processes}
$inventory|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $rawPath -Encoding utf8
$sha=(Get-FileHash -LiteralPath $rawPath -Algorithm SHA256).Hash.ToLowerInvariant()
if($writers.Count -ne 0){throw ('Owned Central writer processes remain active: '+(($writers|ForEach-Object { $_.processId }) -join ','))}
$proof=@{sourceDatabase=$SourceDatabase;port=$Port;postgresPid=$PostgresPid;postgresLauncherPid=$postgresLauncherPid;targetPostgres=$targetIdentity;coordinator=$identity;coordinatorPid=$PID;observedAtUtc=$captured.ToString('O');fileWritersQuiesced=$true;databaseWritersQuiesced=$true;activeOwnedWriterProcessIds=@();observationEvidence=@(@{kind='windows-process-inventory';path=$rawPath;sha256=$sha})}
$proof|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $out -Encoding utf8
Write-Output ('Quiescence proof written: '+$out)
Write-Output ('Native process evidence SHA-256: '+$sha)
Write-Output ('Process inventory rows: '+$processes.Count)
