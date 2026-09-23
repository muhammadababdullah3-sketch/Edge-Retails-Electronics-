param(
    [string]$Solution = "EdgeRetails.sln",
    [string]$DesktopProject = "src/EdgeRetails.Desktop/EdgeRetails.Desktop.csproj",
    [string]$InfrastructureProject = "src/EdgeRetails.Infrastructure/EdgeRetails.Infrastructure.csproj",
    [string]$StartupProject = "src/EdgeRetails.Infrastructure/EdgeRetails.Infrastructure.csproj"
)
$ErrorActionPreference = "Stop"
Write-Warning "LEGACY NON-CERTIFYING HELPER: this script cannot close Sprint 8. Formal certification requires fingerprint-bound Invoke-Sprint8Phase4Closure.ps1 evidence."
Write-Host "Sprint 8 closure does not execute Git commands."

dotnet restore $Solution
Write-Host "Repository-wide format gate intentionally omitted; touched-scope formatting policy applies."
dotnet build $Solution -c Debug --no-restore -warnaserror
dotnet test $Solution -c Debug --no-build --no-restore
dotnet build $Solution -c Release --no-restore -warnaserror
dotnet test $Solution -c Release --no-build --no-restore

dotnet ef migrations has-pending-model-changes --project $InfrastructureProject --startup-project $StartupProject --no-build
& "$PSScriptRoot/Verify-Sprint8Phase1.ps1" -Root "."
& "$PSScriptRoot/Verify-Sprint8Phase2.ps1" -Root "."
& "$PSScriptRoot/Verify-Sprint8Phase3.ps1" -Root "."
& "$PSScriptRoot/Verify-InstallerDataPreservation.ps1" -Root "."
& "$PSScriptRoot/Verify-Sprint8Architecture.ps1" -Root "."

Write-Host "Remaining manual/runtime gates: real PostgreSQL backup/restore integrity test, license scenarios, physical/virtual printer verification, Release EXE smoke test, installer upgrade/uninstall test."
