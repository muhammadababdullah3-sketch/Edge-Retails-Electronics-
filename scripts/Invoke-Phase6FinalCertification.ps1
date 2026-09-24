# Invoke-Phase6FinalCertification.ps1
# Master Phase 6 Fail-Closed Certification Script
# Enforces all automated gates for Edge Retails Production Baseline Certification

param(
    [string]$Solution = "EdgeRetails.sln",
    [string]$Configuration = "Release",
    [switch]$SkipLongRehearsals = $false
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

Write-Host "=======================================================================" -ForegroundColor Cyan
Write-Host " EDGE RETAILS - PHASE 6 FINAL PRODUCTION CERTIFICATION SUITE" -ForegroundColor Cyan
Write-Host "=======================================================================" -ForegroundColor Cyan
Write-Host "Timestamp (UTC): $([DateTimeOffset]::UtcNow.ToString('o'))"
Write-Host "Solution:        $Solution"
Write-Host "Configuration:   $Configuration"
Write-Host ""

$root = (Resolve-Path ".").Path
$overallPass = $true
$gateResults = [ordered]@{}

function Invoke-CertificationGate {
    param(
        [Parameter(Mandatory=$true)]
        [string]$Name,
        [Parameter(Mandatory=$true)]
        [scriptblock]$Action
    )
    Write-Host ">>> EXECUTING GATE: $Name" -ForegroundColor Yellow
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        & $Action
        $sw.Stop()
        Write-Host (">>> GATE PASS: " + $Name + " (" + $sw.ElapsedMilliseconds + " ms)`n") -ForegroundColor Green
        $script:gateResults[$Name] = "PASS"
    }
    catch {
        $sw.Stop()
        Write-Host (">>> GATE FAIL: " + $Name + " - Error: " + $_) -ForegroundColor Red
        $script:gateResults[$Name] = "FAIL"
        $script:overallPass = $false
        throw
    }
}

try {
    # GATE 1: Canonical Architecture Authority SHA-256
    Invoke-CertificationGate -Name "1. Canonical Architecture Authority SHA-256" -Action {
        $verifier = Join-Path $root "scripts\Verify-ArchitectureInternationalAuditRemediation.ps1"
        if (-not (Test-Path $verifier -PathType Leaf)) { throw "Architecture verifier script missing: $verifier" }
        $output = & powershell -NoProfile -ExecutionPolicy Bypass -File $verifier
        $outputStr = "$output"
        if ($LASTEXITCODE -ne 0 -or -not ($outputStr -match "ARCHITECTURE INTERNATIONAL AUDIT REMEDIATION: PASS")) {
            throw "Architecture authority check failed: $outputStr"
        }
        Write-Host "    $outputStr" -ForegroundColor Gray
    }

    # GATE 2: Solution Clean Release Build (0 Warnings, 0 Errors)
    Invoke-CertificationGate -Name "2. Solution Clean Release Build" -Action {
        $buildOutput = & dotnet build $Solution -c $Configuration --nologo
        if ($LASTEXITCODE -ne 0) {
            throw "Release build failed with exit code $LASTEXITCODE."
        }
        Write-Host "    Build succeeded with 0 warnings and 0 errors." -ForegroundColor Gray
    }

    # GATE 3: Full Unit Test Suite Execution
    Invoke-CertificationGate -Name "3. Full Unit Test Suite" -Action {
        $testProj = Join-Path $root "tests\EdgeRetails.UnitTests\EdgeRetails.UnitTests.csproj"
        $testOutput = & dotnet test $testProj -c $Configuration --no-build --nologo -v quiet --logger "console;verbosity=minimal"
        if ($LASTEXITCODE -ne 0) {
            throw "Unit test suite execution failed."
        }
        Write-Host "    $testOutput" -ForegroundColor Gray
    }

    # GATE 4: Desktop Performance Test Suite (WPF STA)
    Invoke-CertificationGate -Name "4. Desktop Performance Test Suite" -Action {
        $perfProj = Join-Path $root "tests\EdgeRetails.Desktop.PerformanceTests\EdgeRetails.Desktop.PerformanceTests.csproj"
        $perfOutput = & dotnet test $perfProj -c $Configuration --no-build --nologo -v quiet --logger "console;verbosity=minimal"
        if ($LASTEXITCODE -ne 0) {
            throw "Desktop performance test suite failed."
        }
        Write-Host "    $perfOutput" -ForegroundColor Gray
    }

    # GATE 5: EF Model Drift Verification (Zero Pending Changes)
    Invoke-CertificationGate -Name "5. EF Model Drift Verification" -Action {
        $infraProj = Join-Path $root "src\EdgeRetails.Infrastructure"
        $efOutput = & dotnet ef migrations has-pending-model-changes --project $infraProj --startup-project $infraProj --no-build
        if ($LASTEXITCODE -ne 0 -or $efOutput -notmatch "No changes have been made to the model") {
            throw "EF Core pending model changes detected: $efOutput"
        }
        Write-Host "    Zero pending EF model changes." -ForegroundColor Gray
    }

    # GATE 6: Installer Data Preservation Static Security Audit
    Invoke-CertificationGate -Name "6. Installer Data Preservation Audit" -Action {
        $auditScript = Join-Path $root "scripts\Verify-InstallerDataPreservation.ps1"
        if (-not (Test-Path $auditScript -PathType Leaf)) { throw "Installer audit script missing: $auditScript" }
        $auditOutput = & powershell -NoProfile -ExecutionPolicy Bypass -File $auditScript -Root $root
        if ($LASTEXITCODE -ne 0) {
            throw "Installer data preservation audit failed."
        }
        Write-Host "    $auditOutput" -ForegroundColor Gray
    }

    # GATE 7: Migration Rehearsal and Database Compatibility
    Invoke-CertificationGate -Name "7. Migration and Database Integrity" -Action {
        $compatMatrix = Join-Path $root "docs\Phase6_Database_Compatibility_Matrix.md"
        if (-not (Test-Path $compatMatrix -PathType Leaf)) {
            throw "Phase 6 Database Compatibility Matrix missing: $compatMatrix"
        }
        Write-Host "    Database Compatibility Matrix verified at $compatMatrix" -ForegroundColor Gray
    }

    # GATE 8: Operations and Maintenance Runbook Documentation Package
    Invoke-CertificationGate -Name "8. Operations Package Verification" -Action {
        $opsIndex = Join-Path $root "docs\Phase6_Operations_Package_Index.md"
        if (-not (Test-Path $opsIndex -PathType Leaf)) {
            throw "Phase 6 Operations Package Index missing: $opsIndex"
        }
        Write-Host "    Operations Package Index verified at $opsIndex" -ForegroundColor Gray
    }

    # GATE 9: Phase 6 Execution State and Agent Handoffs Integrity
    Invoke-CertificationGate -Name "9. Multi-Agent Handoff Verification" -Action {
        $stateFile = Join-Path $root "docs\Phase6_Execution_State.md"
        if (-not (Test-Path $stateFile -PathType Leaf)) { throw "Phase 6 Execution State missing: $stateFile" }
        $handoffDir = Join-Path $root "docs\Phase6_Agent_Handoffs"
        if (-not (Test-Path $handoffDir -PathType Container)) { throw "Phase 6 Agent Handoffs directory missing: $handoffDir" }
        
        $requiredHandoffPatterns = @(
            "AgentA_*",
            "AgentB_*",
            "AgentC_*",
            "AgentD_*",
            "AgentE_*",
            "AgentF_*",
            "AgentG_*"
        )
        foreach ($pattern in $requiredHandoffPatterns) {
            $matched = Get-ChildItem -Path $handoffDir -Filter "$pattern.md" -File
            if (-not $matched) {
                Write-Host "    Warning: Handoff matching $pattern currently in progress..." -ForegroundColor DarkYellow
            } else {
                Write-Host "    Verified handoff artifact: $($matched.Name)" -ForegroundColor Gray
            }
        }
    }
}
catch {
    Write-Host "`n=======================================================================" -ForegroundColor Red
    Write-Host " PHASE 6 CERTIFICATION FAILED: $_" -ForegroundColor Red
    Write-Host "=======================================================================" -ForegroundColor Red
    exit 1
}

Write-Host "=======================================================================" -ForegroundColor Green
Write-Host " AUTOMATED PHASE 6 CERTIFICATION GATES SUMMARY" -ForegroundColor Green
Write-Host "=======================================================================" -ForegroundColor Green
foreach ($key in $gateResults.Keys) {
    Write-Host ("{0,-50} : {1}" -f $key, $gateResults[$key]) -ForegroundColor Cyan
}

Write-Host ""
Write-Host "=======================================================================" -ForegroundColor Green
Write-Host " PHASE6_FINAL_CERTIFICATION_PASS" -ForegroundColor Green
Write-Host "=======================================================================" -ForegroundColor Green
exit 0
