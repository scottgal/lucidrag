# DataSummarizer Comprehensive Test Script
# Run from Mostlylucid.DataSummarizer directory

$ErrorActionPreference = "Stop"
$testDir = "test-output"
$vectorDb = "$testDir/test-registry.duckdb"

# Colors for output
function Write-Success { param($msg) Write-Host "[PASS] $msg" -ForegroundColor Green }
function Write-Fail { param($msg) Write-Host "[FAIL] $msg" -ForegroundColor Red }
function Write-Info { param($msg) Write-Host "[INFO] $msg" -ForegroundColor Cyan }
function Write-Test { param($msg) Write-Host "`n=== $msg ===" -ForegroundColor Yellow }

# Track results
$passed = 0
$failed = 0
$errors = @()

# Clean up previous test output
Write-Info "Setting up test environment..."
if (Test-Path $testDir) { Remove-Item -Recurse -Force $testDir }
New-Item -ItemType Directory -Path $testDir | Out-Null

# Build first
Write-Test "Building project"
$buildOutput = dotnet build --no-restore 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Fail "Build failed"
    $buildOutput | Write-Host
    exit 1
}
Write-Success "Build succeeded"

# ============================================================================
# TEST 1: Profile Bank Churn CSV
# ============================================================================
Write-Test "TEST 1: Profile Bank_Churn.csv"
$output = dotnet run -- -f "sampledata/Bank+Customer+Churn/Bank_Churn.csv" --no-llm -o "$testDir/bank-report.md" 2>&1 | Out-String

# Validate output makes sense
if ($output -match "10,000 rows" -and $output -match "13 columns") {
    Write-Success "Row/column count correct (10,000 rows, 13 columns)"
    $passed++
} else {
    Write-Fail "Row/column count incorrect"
    $failed++
    $errors += "Bank Churn: Expected 10,000 rows, 13 columns"
}

if ($output -match "CreditScore.*Numeric" -and $output -match "Geography.*Categorical") {
    Write-Success "Column type inference correct"
    $passed++
} else {
    Write-Fail "Column type inference incorrect"
    $failed++
}

if ($output -match "Age.*outliers") {
    Write-Success "Outlier detection working (Age column)"
    $passed++
} else {
    Write-Fail "Outlier detection not working"
    $failed++
}

# ============================================================================
# TEST 2: Profile CO2 Emissions
# ============================================================================
Write-Test "TEST 2: Profile CO2 Emissions"
$output = dotnet run -- -f "sampledata/CO2+Emissions/visualizing_global_co2_data.csv" --no-llm 2>&1 | Out-String

if ($output -match "rows" -and $output -match "columns") {
    Write-Success "CO2 data profiled successfully"
    $passed++
} else {
    Write-Fail "CO2 profiling failed"
    $failed++
}

# ============================================================================
# TEST 3: Profile Hospital Patients
# ============================================================================
Write-Test "TEST 3: Profile Hospital Patients"
$output = dotnet run -- -f "sampledata/Hospital+Patient+Records/patients.csv" --no-llm 2>&1 | Out-String

if ($output -match "rows" -and $output -match "columns") {
    Write-Success "Hospital patients profiled"
    $passed++
    
    # Check for expected columns
    if ($output -match "BIRTHDATE|FIRST|LAST") {
        Write-Success "Expected patient columns found"
        $passed++
    } else {
        Write-Fail "Patient columns not detected properly"
        $failed++
    }
} else {
    Write-Fail "Hospital patients profiling failed"
    $failed++
}

# ============================================================================
# TEST 4: Profile MoMA Artworks (larger dataset)
# ============================================================================
Write-Test "TEST 4: Profile MoMA Artworks"
$output = dotnet run -- -f "sampledata/MoMA+Art+Collection/Artworks.csv" --no-llm 2>&1 | Out-String

if ($output -match "rows" -and $output -match "columns") {
    Write-Success "MoMA Artworks profiled"
    $passed++
} else {
    Write-Fail "MoMA Artworks profiling failed"
    $failed++
}

# ============================================================================
# TEST 5: Profile Wine Reviews (130k rows - performance test)
# ============================================================================
Write-Test "TEST 5: Profile Wine Reviews (130k rows - performance)"
$sw = [Diagnostics.Stopwatch]::StartNew()
$output = dotnet run -- -f "sampledata/winemag-data-130k-v2.csv/winemag-data-130k-v2.csv" --no-llm 2>&1 | Out-String
$sw.Stop()

if ($output -match "rows" -and $output -match "columns") {
    Write-Success "Wine reviews profiled in $($sw.Elapsed.TotalSeconds.ToString('F1'))s"
    $passed++
    
    if ($sw.Elapsed.TotalSeconds -lt 30) {
        Write-Success "Performance acceptable (<30s for 130k rows)"
        $passed++
    } else {
        Write-Fail "Performance too slow (>30s)"
        $failed++
    }
} else {
    Write-Fail "Wine reviews profiling failed"
    $failed++
}

# ============================================================================
# TEST 6: Excel file support
# ============================================================================
Write-Test "TEST 6: Excel file support"
$output = dotnet run -- -f "sampledata/Bank+Customer+Churn/Bank_Churn_Messy.xlsx" --no-llm 2>&1 | Out-String

if ($output -match "rows" -and $output -match "columns") {
    Write-Success "Excel file profiled"
    $passed++
} else {
    Write-Fail "Excel profiling failed"
    Write-Info $output
    $failed++
}

# ============================================================================
# TEST 7: Profile subcommand with JSON output
# ============================================================================
Write-Test "TEST 7: Profile subcommand"
dotnet run -- profile --file "sampledata/Bank+Customer+Churn/Bank_Churn.csv" --output "$testDir/bank.profile.json" 2>&1 | Out-Null

if (Test-Path "$testDir/bank.profile.json") {
    $json = Get-Content "$testDir/bank.profile.json" -Raw | ConvertFrom-Json
    
    if ($json.Count -ge 1 -and $json[0].RowCount -eq 10000) {
        Write-Success "Profile JSON correct (10000 rows)"
        $passed++
    } else {
        Write-Fail "Profile JSON row count wrong"
        $failed++
    }
    
    if ($json[0].Columns.Count -eq 13) {
        Write-Success "Profile JSON has 13 columns"
        $passed++
    } else {
        Write-Fail "Profile JSON column count wrong: $($json[0].Columns.Count)"
        $failed++
    }
} else {
    Write-Fail "Profile JSON not created"
    $failed++
}

# ============================================================================
# TEST 8: Synth subcommand
# ============================================================================
Write-Test "TEST 8: Synth subcommand"
dotnet run -- synth --profile "$testDir/bank.profile.json" --synthesize-to "$testDir/synthetic.csv" --synthesize-rows 500 2>&1 | Out-Null

if (Test-Path "$testDir/synthetic.csv") {
    $lines = Get-Content "$testDir/synthetic.csv"
    $header = $lines[0]
    $rowCount = $lines.Count - 1  # minus header
    
    if ($rowCount -eq 500) {
        Write-Success "Synthetic CSV has correct row count (500)"
        $passed++
    } else {
        Write-Fail "Synthetic CSV row count wrong: $rowCount"
        $failed++
    }
    
    # Check header has expected columns
    if ($header -match "CustomerId" -and $header -match "CreditScore" -and $header -match "Geography") {
        Write-Success "Synthetic CSV has correct columns"
        $passed++
    } else {
        Write-Fail "Synthetic CSV columns wrong"
        $failed++
    }
    
    # Check a data row makes sense
    $dataRow = $lines[1] -split ','
    $creditScore = [double]$dataRow[2]
    if ($creditScore -ge 300 -and $creditScore -le 900) {
        Write-Success "Synthetic CreditScore in valid range ($creditScore)"
        $passed++
    } else {
        Write-Fail "Synthetic CreditScore out of range: $creditScore"
        $failed++
    }
} else {
    Write-Fail "Synthetic CSV not created"
    $failed++
}

# ============================================================================
# TEST 9: Validate subcommand
# ============================================================================
Write-Test "TEST 9: Validate subcommand"
$valOutput = dotnet run -- validate --source "sampledata/Bank+Customer+Churn/Bank_Churn.csv" --target "$testDir/synthetic.csv" --output "$testDir/validation.json" 2>&1 | Out-String

if (Test-Path "$testDir/validation.json") {
    $val = Get-Content "$testDir/validation.json" -Raw | ConvertFrom-Json
    
    if ($val.Columns.Count -eq 13) {
        Write-Success "Validation has all 13 column comparisons"
        $passed++
    } else {
        Write-Fail "Validation column count wrong: $($val.Columns.Count)"
        $failed++
    }
    
    # Check drift score is reasonable (synth should be similar)
    if ($val.DriftScore -ge 0 -and $val.DriftScore -le 1) {
        Write-Success "Drift score in valid range: $($val.DriftScore)"
        $passed++
    } else {
        Write-Fail "Drift score invalid: $($val.DriftScore)"
        $failed++
    }
    
    # Check categorical overlap
    $geoCol = $val.Columns | Where-Object { $_.Name -eq "Geography" }
    if ($geoCol -and $geoCol.TopOverlap -eq 1.0) {
        Write-Success "Categorical TopOverlap correct (Geography = 1.0)"
        $passed++
    } else {
        Write-Fail "Categorical TopOverlap wrong"
        $failed++
    }
} else {
    Write-Fail "Validation JSON not created"
    $failed++
}

# ============================================================================
# TEST 10: Ingest multiple files
# ============================================================================
Write-Test "TEST 10: Ingest multiple files into registry"
$ingestOutput = dotnet run -- --ingest-dir "sampledata/Bank+Customer+Churn" --ingest-dir "sampledata/CO2+Emissions" --no-llm --vector-db $vectorDb 2>&1 | Out-String

if ($ingestOutput -match "Ingesting" -and $ingestOutput -match "complete") {
    Write-Success "Ingest completed"
    $passed++
} else {
    Write-Fail "Ingest failed"
    Write-Info $ingestOutput
    $failed++
}

# ============================================================================
# TEST 11: Registry query (no LLM)
# ============================================================================
Write-Test "TEST 11: Registry query (no LLM)"
$regOutput = dotnet run -- --registry-query "What columns relate to customer churn?" --vector-db $vectorDb --no-llm 2>&1 | Out-String

if ($regOutput -match "Registry" -or $regOutput -match "context" -or $regOutput -match "Answer") {
    Write-Success "Registry query returned results"
    $passed++
} else {
    Write-Fail "Registry query failed"
    Write-Info $regOutput
    $failed++
}

# ============================================================================
# TEST 12: Handle edge cases - empty/small files
# ============================================================================
Write-Test "TEST 12: Edge case - small CSV"
"A,B`n1,2" | Out-File -FilePath "$testDir/tiny.csv" -Encoding utf8
$tinyOutput = dotnet run -- -f "$testDir/tiny.csv" --no-llm 2>&1 | Out-String

if ($tinyOutput -match "1 rows" -and $tinyOutput -match "2 columns") {
    Write-Success "Tiny CSV handled correctly"
    $passed++
} else {
    Write-Fail "Tiny CSV handling failed"
    Write-Info $tinyOutput
    $failed++
}

# ============================================================================
# TEST 13: Handle files with special characters in values
# ============================================================================
Write-Test "TEST 13: CSV with special characters"
@"
Name,Description,Value
"John, Jr.",He said "hello",100
Jane's,Multi
line,200
"@ | Out-File -FilePath "$testDir/special.csv" -Encoding utf8

$specialOutput = dotnet run -- -f "$testDir/special.csv" --no-llm 2>&1 | Out-String

if ($specialOutput -match "rows" -or $specialOutput -match "error" -eq $false) {
    Write-Success "Special characters handled"
    $passed++
} else {
    Write-Fail "Special characters caused issues"
    $failed++
}

# ============================================================================
# TEST 14: Global Electronics Retailer (multiple files)
# ============================================================================
Write-Test "TEST 14: Profile Global Electronics Retailer Sales"
$salesOutput = dotnet run -- -f "sampledata/Global+Electronics+Retailer/Sales.csv" --no-llm 2>&1 | Out-String

if ($salesOutput -match "rows" -and $salesOutput -match "columns") {
    Write-Success "Sales CSV profiled"
    $passed++
    
    # Check for date columns
    if ($salesOutput -match "Date" -or $salesOutput -match "DateTime") {
        Write-Success "Date columns detected"
        $passed++
    } else {
        Write-Info "No date columns detected (may be ok)"
    }
} else {
    Write-Fail "Sales CSV profiling failed"
    $failed++
}

# ============================================================================
# TEST 15: UFO Sightings (test text patterns)
# ============================================================================
Write-Test "TEST 15: UFO Sightings (text patterns)"
$ufoOutput = dotnet run -- -f "sampledata/ufo_sightings_scrubbed.csv/ufo_sightings_scrubbed.csv" --no-llm 2>&1 | Out-String

if ($ufoOutput -match "rows" -and $ufoOutput -match "columns") {
    Write-Success "UFO sightings profiled"
    $passed++
} else {
    Write-Fail "UFO sightings profiling failed"
    $failed++
}

# ============================================================================
# SUMMARY
# ============================================================================
Write-Host "`n"
Write-Host "=" * 60 -ForegroundColor White
Write-Host "TEST SUMMARY" -ForegroundColor White
Write-Host "=" * 60 -ForegroundColor White
Write-Host "Passed: $passed" -ForegroundColor Green
Write-Host "Failed: $failed" -ForegroundColor Red

if ($errors.Count -gt 0) {
    Write-Host "`nErrors:" -ForegroundColor Red
    $errors | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
}

if ($failed -eq 0) {
    Write-Host "`nAll tests passed!" -ForegroundColor Green
    exit 0
} else {
    Write-Host "`nSome tests failed!" -ForegroundColor Red
    exit 1
}
