# Invoke-Phase6FinalCertification.ps1
# Master Phase 6 Fail-Closed Certification Script
# Enforces all automated gates for Edge Retails Production Baseline Certification

param(
    [string]$Solution = "EdgeRetails.sln",
    [string]$Configuration = "Release",
    [switch]$SkipLongRehearsals = $false,
    [switch]$AllowBlockedEnvironment = $false
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
$hasBlocked = $false
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
        $res = & $Action
        $sw.Stop()
        if ($res -is [string] -and $res -match "^BLOCKED_ENVIRONMENT") {
            Write-Host (">>> GATE BLOCKED_ENVIRONMENT: " + $Name + " (" + $sw.ElapsedMilliseconds + " ms) - " + $res + "`n") -ForegroundColor Yellow
            $script:gateResults[$Name] = "BLOCKED_ENVIRONMENT"
            $script:hasBlocked = $true
        } else {
            Write-Host (">>> GATE PASS: " + $Name + " (" + $sw.ElapsedMilliseconds + " ms)`n") -ForegroundColor Green
            $script:gateResults[$Name] = "PASS"
        }
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
    Invoke-CertificationGate -Name "2. Solution Clean Release Build (Zero Warnings/Errors)" -Action {
        $buildOutput = & dotnet build $Solution -c $Configuration --nologo -warnaserror
        if ($LASTEXITCODE -ne 0) {
            throw "Release build failed with exit code $LASTEXITCODE or emitted compiler warnings."
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
    Invoke-CertificationGate -Name "7. Migration Rehearsal and Database Integrity" -Action {
        $compatMatrix = Join-Path $root "docs\Phase6_Database_Compatibility_Matrix.md"
        if (-not (Test-Path $compatMatrix -PathType Leaf)) {
            throw "Phase 6 Database Compatibility Matrix missing: $compatMatrix"
        }
        
        $infraProj = Join-Path $root "src\EdgeRetails.Infrastructure"
        # 1. Rehearse idempotent migration script generation
        $scriptOutput = & dotnet ef migrations script --project $infraProj --startup-project $infraProj --idempotent --no-build
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($scriptOutput)) {
            throw "Idempotent migration script generation failed."
        }
        Write-Host "    Idempotent migration script generated successfully." -ForegroundColor Gray

        # 2. Check for isolated rehearsal PostgreSQL environment
        $rehearsalScript = Join-Path $root "scripts\Invoke-Phase6DatabaseMigrationRehearsal.ps1"
        $pgBin = $env:PG_BIN
        if ([string]::IsNullOrWhiteSpace($pgBin)) {
            $pgBin = "C:\Program Files\PostgreSQL\18\bin"
        }

        $canRehearseCluster = (Test-Path (Join-Path $pgBin "initdb.exe") -PathType Leaf) -and (Test-Path $rehearsalScript -PathType Leaf)
        $connStr = $env:EDGE_RETAILS_TEST_DB
        if ([string]::IsNullOrWhiteSpace($connStr)) {
            $connStr = $env:EDGE_RETAILS_REHEARSAL_DB
        }

        if ($canRehearseCluster -and -not $SkipLongRehearsals) {
            Write-Host "    Executing isolated PostgreSQL 18 cluster migration rehearsal..." -ForegroundColor Gray
            $rehearsalOutput = & powershell -NoProfile -ExecutionPolicy Bypass -File $rehearsalScript -PgBin $pgBin -SolutionRoot $root
            if ($LASTEXITCODE -ne 0) {
                throw "PostgreSQL 18 database migration rehearsal failed."
            }
            Write-Host "    PostgreSQL 18 database migration rehearsal PASSED with full schema and compatibility verification." -ForegroundColor Gray
            return "PASS"
        }
        elseif (-not [string]::IsNullOrWhiteSpace($connStr)) {
            # Dedicated test connection provided
            # Verify target database is explicitly disposable/non-production
            if ($connStr -notmatch "_test|_rehearsal|_disposable|_temp" -or $connStr -match "prod|production") {
                throw "Target database connection string does not appear to be an isolated disposable/test database: $connStr"
            }
            Write-Host "    Executing EF Core database update on test database: $connStr..." -ForegroundColor Gray
            $updateOutput = & dotnet ef database update --project $infraProj --startup-project $infraProj --context EdgeRetailsDbContext --connection $connStr
            if ($LASTEXITCODE -ne 0) {
                throw "Migration rehearsal on test connection failed: $updateOutput"
            }
            Write-Host "    Migration rehearsal on test connection PASSED." -ForegroundColor Gray
            return "PASS"
        }
        else {
            Write-Host "    [BLOCKED_ENVIRONMENT] No local PostgreSQL 18 tools or disposable test database (EDGE_RETAILS_TEST_DB) detected for runtime migration rehearsal." -ForegroundColor Yellow
            return "BLOCKED_ENVIRONMENT: SKIPPED_NO_REHEARSAL_POSTGRES"
        }
    }

    # GATE 8: Operations and Maintenance Runbook Documentation Package
    Invoke-CertificationGate -Name "8. Operations Package & Runbook Consistency" -Action {
        $opsIndex = Join-Path $root "docs\Phase6_Operations_Package_Index.md"
        if (-not (Test-Path $opsIndex -PathType Leaf)) {
            throw "Phase 6 Operations Package Index missing: $opsIndex"
        }
        $deploymentGuide = Join-Path $root "docs\operations\Installer_Deployment_Guide.md"
        if (-not (Test-Path $deploymentGuide -PathType Leaf)) {
            throw "Installer Deployment Guide missing: $deploymentGuide"
        }
        $licensingGuide = Join-Path $root "docs\operations\Production_Licensing_Standard_Operating_Procedure.md"
        if (-not (Test-Path $licensingGuide -PathType Leaf)) {
            throw "Production Licensing SOP missing: $licensingGuide"
        }

        # Verify referenced configuration keys in runbooks match codebase
        $licensingContent = Get-Content $licensingGuide -Raw
        if ($licensingContent -notmatch "EDGE_RETAILS_LICENSE_PUBLIC_KEY") {
            throw "Licensing SOP missing required reference to EDGE_RETAILS_LICENSE_PUBLIC_KEY"
        }
        if ($licensingContent -notmatch "\.erlic") {
            throw "Licensing SOP missing authoritative .erlic extension reference"
        }

        # Verify no hardcoded sample credentials in deployment guide
        $deployContent = Get-Content $deploymentGuide -Raw
        if ($deployContent -match "SecureProductionPassword18!") {
            throw "Installer Deployment Guide contains insecure hardcoded sample password!"
        }
        Write-Host "    Operations package and runbook consistency verified." -ForegroundColor Gray
    }

    # GATE 9: Production Runtime Composition & Licensing Verification
    Invoke-CertificationGate -Name "9. Production Composition & Licensing Verification" -Action {
        # Execute dedicated production forensic remediation tests
        $testProj = Join-Path $root "tests\EdgeRetails.UnitTests\EdgeRetails.UnitTests.csproj"
        $compOutput = & dotnet test $testProj -c $Configuration --no-build --nologo -v quiet --filter "FullyQualifiedName~ProductionForensicRemediationTests" --logger "console;verbosity=minimal"
        if ($LASTEXITCODE -ne 0) {
            throw "Production composition & licensing tests failed: $compOutput"
        }
        Write-Host "    $compOutput" -ForegroundColor Gray

        # Also verify multi-agent handoff artifacts
        $stateFile = Join-Path $root "docs\Phase6_Execution_State.md"
        if (-not (Test-Path $stateFile -PathType Leaf)) { throw "Phase6 Execution State missing: $stateFile" }
        $handoffDir = Join-Path $root "docs\Phase6_Agent_Handoffs"
        if (-not (Test-Path $handoffDir -PathType Container)) { throw "Phase6 Agent Handoffs directory missing: $handoffDir" }
        
        $requiredHandoffPatterns = @(
            "AgentA_*", "AgentB_*", "AgentC_*", "AgentD_*", "AgentE_*", "AgentF_*", "AgentG_*"
        )
        foreach ($pattern in $requiredHandoffPatterns) {
            $matched = @(Get-ChildItem -Path $handoffDir -Filter "$pattern.md" -File)
            if ($matched.Count -eq 0) {
                throw "Mandatory handoff artifact missing matching pattern '$pattern.md' in $handoffDir"
            }
            foreach ($m in $matched) {
                Write-Host "    Verified handoff artifact: $($m.Name)" -ForegroundColor Gray
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

Write-Host "=======================================================================" -ForegroundColor Cyan
Write-Host " AUTOMATED PHASE 6 CERTIFICATION GATES SUMMARY" -ForegroundColor Cyan
Write-Host "=======================================================================" -ForegroundColor Cyan
foreach ($key in $gateResults.Keys) {
    $resColor = switch ($gateResults[$key]) {
        "PASS" { "Green" }
        "BLOCKED_ENVIRONMENT" { "Yellow" }
        Default { "Red" }
    }
    Write-Host ("{0,-50} : {1}" -f $key, $gateResults[$key]) -ForegroundColor $resColor
}

Write-Host ""
if (-not $overallPass) {
    Write-Host "=======================================================================" -ForegroundColor Red
    Write-Host " PHASE6_FINAL_CERTIFICATION_FAIL" -ForegroundColor Red
    Write-Host "=======================================================================" -ForegroundColor Red
    exit 1
}

if ($hasBlocked) {
    if ($AllowBlockedEnvironment) {
        Write-Host "=======================================================================" -ForegroundColor Yellow
        Write-Host " CERTIFIED_WITH_EXPLICIT_ENVIRONMENT_BLOCKED_GATES" -ForegroundColor Yellow
        Write-Host "=======================================================================" -ForegroundColor Yellow
        exit 0
    }
    else {
        Write-Host "=======================================================================" -ForegroundColor Yellow
        Write-Host " PHASE6_CERTIFICATION_BLOCKED_ENVIRONMENT" -ForegroundColor Yellow
        Write-Host "=======================================================================" -ForegroundColor Yellow
        Write-Host "One or more mandatory runtime gates were blocked due to missing environment requirements." -ForegroundColor Yellow
        Write-Host "Run with -AllowBlockedEnvironment to permit non-production development progression." -ForegroundColor Yellow
        exit 2
    }
}

Write-Host "=======================================================================" -ForegroundColor Green
Write-Host " PHASE6_FINAL_CERTIFICATION_PASS" -ForegroundColor Green
Write-Host "=======================================================================" -ForegroundColor Green
exit 0
