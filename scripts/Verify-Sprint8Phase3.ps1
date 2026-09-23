param([string]$Root = ".")
$ErrorActionPreference = "Stop"
$failures = @()
function Require([string]$Path,[string]$Pattern,[string]$Message) {
  $full=Join-Path $Root $Path
  if(-not(Test-Path $full)){ $script:failures += "Missing file: $Path"; return }
  if(-not(Select-String -Path $full -SimpleMatch -Pattern $Pattern -Quiet)){ $script:failures += $Message }
}
function Reject([string]$Path,[string]$Pattern,[string]$Message) {
  $full=Join-Path $Root $Path
  if((Test-Path $full) -and (Select-String -Path $full -SimpleMatch -Pattern $Pattern -Quiet)){ $script:failures += $Message }
}

Require "src/EdgeRetails.Desktop/Production/Printing/WpfProductionPrintEngine.cs" "PrintTicket" "Physical printing does not use a PrintTicket."
Require "src/EdgeRetails.Desktop/Production/Printing/WpfProductionPrintEngine.cs" "PageMediaSize" "Physical printing does not enforce media size."
Require "src/EdgeRetails.Desktop/Production/Printing/WpfProductionPrintEngine.cs" "MergeAndValidatePrintTicket" "Driver-validated PrintTicket enforcement is missing."
Require "src/EdgeRetails.Desktop/Production/Printing/WpfProductionPrintEngine.cs" "PageImageableArea" "Driver-reported printable margins are not honored."
Require "src/EdgeRetails.Desktop/Production/Printing/WpfProductionPrintEngine.cs" "CopyCount" "Copies are not submitted as one job-level PrintTicket copy count."
Require "src/EdgeRetails.Desktop/Production/Printing/WpfProductionPrintEngine.cs" "MaxCopyCount" "Printer copy capability is not enforced."
Require "src/EdgeRetails.Desktop/Production/Printing/WpfProductionPrintEngine.cs" "GetPrintCapabilities" "Printer capabilities are not checked."
Require "src/EdgeRetails.Desktop/Production/Printing/WpfProductionPrintEngine.cs" "print.media_not_supported" "Unsupported media does not fail closed."
Require "src/EdgeRetails.Desktop/Production/Printing/WpfPrinterProfileValidator.cs" "MatchMedia" "Printer profiles are not validated against driver media capabilities."
Require "src/EdgeRetails.Application/Production/Printing/PrintingContracts.cs" "PrintRequestMode" "Retry/reprint semantics are not explicit."
Require "src/EdgeRetails.Application/Production/Printing/PrintingContracts.cs" "OutcomeUnknown" "Physical print ambiguous-outcome state is missing."
Require "src/EdgeRetails.Application/Production/Printing/PrintingHandlers.cs" "AlreadyCompleted" "Succeeded jobs can still be automatically reprinted on retry."
Require "src/EdgeRetails.Application/Production/Printing/PrintingHandlers.cs" 'CopyLabel = "REPRINT"' "Explicit reprints are not marked on the rendered document."
Require "src/EdgeRetails.Infrastructure/Production/Printing/JsonPrintJobStore.cs" "IPrintJobStore" "Durable print job adapter is missing."
Require "src/EdgeRetails.Application/Production/Printing/ProductionDocumentSourceRouter.cs" "All canonical production document sources must be registered" "All-seven document source registration is not fail-closed."
Require "tests/EdgeRetails.UnitTests/Sprint8/Sprint8PrintingTests.cs" "RetryOfSucceededJob_IsIdempotentAndDoesNotPrintTwice" "Missing retry idempotency regression test."
Require "tests/EdgeRetails.UnitTests/Sprint8/Sprint8PrintingTests.cs" "ExplicitReprint_CreatesNewJob_AndRendersReprintMarker" "Missing explicit reprint regression test."
Require "tests/EdgeRetails.UnitTests/Sprint8/Sprint8PrintingTests.cs" "AmbiguousPhysicalOutcome_IsPersistedAndBlocksAutomaticRetry" "Missing ambiguous spool outcome regression test."
Require "scripts/Publish-Release.ps1" 'dotnet restore $DesktopProject -r $Runtime' "RID-specific Desktop restore is missing before --no-restore publish."
Require "scripts/Publish-Release.ps1" "ApplicationIcon" "Release pipeline does not enforce application icon metadata."
Require "scripts/Publish-Release.ps1" 'Resolve-Path $iconPath' "Application icon path is not canonicalized before MSBuild/WiX use."
Require "scripts/Publish-Release.ps1" "Verify-InstallerDataPreservation" "Release build does not run installer data-preservation audit."
Require "scripts/Publish-Release.ps1" "installerPlatform" "Release manifest does not record installer architecture."
Require "scripts/Publish-Release.ps1" "release-manifest.json" "Release artifact manifest is missing."
Require "scripts/Publish-Release.ps1" "SHA256SUMS" "Release artifact hashes are missing."
Require "installer/EdgeRetails.Setup/Package.wxs" "MajorUpgrade" "MSI does not explicitly author safe major-upgrade behavior."
Require "installer/EdgeRetails.Setup/Package.wxs" "ARPPRODUCTICON" "MSI Programs-and-Features icon metadata is missing."
Require "installer/EdgeRetails.Setup/Package.wxs" "ProgramFiles64Folder" "MSI application ownership is not constrained to Program Files."
Require "installer/EdgeRetails.Bootstrapper/Bundle.wxs" "MsiPackage" "Burn bundle does not chain the MSI."
Require "installer/EdgeRetails.Bootstrapper/Bundle.wxs" "IconSourceFile" "Burn setup executable icon metadata is missing."
Require "scripts/Test-InstallerLifecyclePreservation.ps1" "upgrade/uninstall preservation PASS" "Installer lifecycle preservation test is missing."
Require "scripts/Verify-InstallerDataPreservation.ps1" "Installer data-preservation static audit PASS" "Installer preservation verifier is missing."
Reject "installer/EdgeRetails.Setup/Package.wxs" "ProgramDataFolder" "Installer owns ProgramData, which risks deleting operational data on uninstall."
Reject "installer/EdgeRetails.Setup/Package.wxs" "PostgreSQL" "Installer must not own PostgreSQL data/runtime unless an existing project policy explicitly requires it."

$kinds = @('PosSaleReceipt','SaleReturnReceipt','PurchaseDocument','PurchaseReturnDocument','ThakaMaterialChallan','ThakaPaymentReceipt','FinalSettlementStatement')
foreach($kind in $kinds){ Require "src/EdgeRetails.Application/Production/Printing/PrintingContracts.cs" $kind "Missing production document kind: $kind" }

if($failures.Count -gt 0){ $failures | ForEach-Object { Write-Error $_ }; exit 1 }
Write-Host "Sprint 8 Phase 3 static forensic audit PASS"
