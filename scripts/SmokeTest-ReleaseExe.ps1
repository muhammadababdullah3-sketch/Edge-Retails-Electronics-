param(
    [Parameter(Mandatory=$true)][string]$ExePath,
    [int]$ObservationSeconds = 8,
    [int]$ShutdownSeconds = 15
)
$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
if (-not (Test-Path $ExePath -PathType Leaf)) { throw "Release EXE not found: $ExePath" }
if ($ObservationSeconds -lt 3) { throw "ObservationSeconds must be at least 3." }
if ($ShutdownSeconds -lt 1) { throw "ShutdownSeconds must be at least 1." }

$before = (Get-Item $ExePath).LastWriteTimeUtc
$process = Start-Process -FilePath $ExePath -PassThru
try {
    Start-Sleep -Seconds $ObservationSeconds
    $process.Refresh()
    if ($process.HasExited) { throw "Release EXE exited during smoke window with code $($process.ExitCode)." }
    if ((Get-Item $ExePath).LastWriteTimeUtc -ne $before) { throw "Release executable changed during smoke test." }
    if ($process.MainWindowHandle -eq 0) {
        throw "Release EXE remained alive but exposed no interactive WPF window during the smoke window."
    }

    if (-not $process.CloseMainWindow()) {
        throw "Release EXE did not accept a normal WPF window-close request."
    }
    if (-not $process.WaitForExit($ShutdownSeconds * 1000)) {
        throw "Release EXE did not shut down cleanly within $ShutdownSeconds second(s) after a normal window-close request."
    }
    if ($process.ExitCode -ne 0) {
        throw "Release EXE returned non-zero exit code $($process.ExitCode) during clean shutdown."
    }

    Write-Host "Release EXE smoke PASS: startup remained alive, WPF window was available, and normal shutdown completed cleanly."
}
finally {
    if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
}
