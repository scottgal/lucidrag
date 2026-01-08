# Test script for DataSummarizer interactive mode commands
# Usage: .\test-interactive.ps1 [datafile]

param(
    [string]$DataFile = "sampledata/Bank+Customer+Churn/Bank_Churn.csv",
    [switch]$Verbose
)

$ErrorActionPreference = "Stop"
$script:TestsPassed = 0
$script:TestsFailed = 0

function Write-TestHeader($name) {
    Write-Host "`n$('=' * 60)" -ForegroundColor Cyan
    Write-Host "TEST: $name" -ForegroundColor Cyan
    Write-Host "$('=' * 60)" -ForegroundColor Cyan
}

function Write-Pass($msg) {
    Write-Host "[PASS] $msg" -ForegroundColor Green
    $script:TestsPassed++
}

function Write-Fail($msg) {
    Write-Host "[FAIL] $msg" -ForegroundColor Red
    $script:TestsFailed++
}

function Remove-AnsiCodes($text) {
    # Remove ANSI escape sequences
    $text -replace '\x1b\[[0-9;]*m', '' -replace '\[[\d;]*m', ''
}

function Test-Command($name, $commands, $expectedPatterns) {
    Write-TestHeader $name
    
    $input = ($commands + "/exit") -join "`n"
    $rawOutput = $input | dotnet run --project . -- -f $DataFile -i --no-llm 2>&1 | Out-String
    $output = Remove-AnsiCodes $rawOutput
    
    if ($Verbose) {
        Write-Host $output -ForegroundColor Gray
    }
    
    $allPassed = $true
    foreach ($pattern in $expectedPatterns) {
        if ($output -match $pattern) {
            Write-Pass "Found: $pattern"
        } else {
            Write-Fail "Missing: $pattern"
            $allPassed = $false
        }
    }
    
    return $allPassed
}

# Change to script directory
Push-Location $PSScriptRoot

try {
    Write-Host "`nDataSummarizer Interactive Mode Test Suite" -ForegroundColor Yellow
    Write-Host "Data file: $DataFile" -ForegroundColor Yellow
    Write-Host "Started: $(Get-Date)" -ForegroundColor Yellow

    # Test 1: Show commands with /
    Test-Command "Show Commands (/)" @("/") @(
        "Commands",
        "/help",
        "/tools",
        "/profiles",
        "/columns",
        "/exit"
    )

    # Test 2: Show commands with /help
    Test-Command "Show Commands (/help)" @("/help") @(
        "Commands",
        "/profile <name>",
        "/column <name>"
    )

    # Test 3: List tools
    Test-Command "List Tools (/tools)" @("/tools") @(
        "Analytics Tools",
        "Segmentation",
        "Anomaly",
        "segment_audience|Audience Segmentation"
    )

    # Test 4: List profiles
    Test-Command "List Profiles (/profiles)" @("/profiles") @(
        "Output Profiles",
        "Default",
        "Brief",
        "Detailed",
        "Tool",
        "Markdown",
        "active"
    )

    # Test 5: Switch profile
    Test-Command "Switch Profile (/profile Brief)" @("/profile Brief") @(
        "Switched to 'Brief' profile",
        "Quick overview"
    )

    # Test 6: Show current profile
    Test-Command "Current Profile (/profile)" @("/profile") @(
        "Current profile:",
        "Default"
    )

    # Test 7: Invalid profile
    Test-Command "Invalid Profile (/profile NonExistent)" @("/profile NonExistent") @(
        "Unknown profile",
        "/profiles"
    )

    # Test 8: Show status
    Test-Command "Session Status (/status)" @("/status") @(
        "Session Status",
        "File:",
        "Rows:",
        "Columns:",
        "Session:",
        "Profile:"
    )

    # Test 9: Show summary
    Test-Command "Data Summary (/summary)" @("/summary") @(
        "Data Summary",
        "Rows:",
        "Columns:",
        "Types:",
        "numeric"
    )

    # Test 10: List columns
    Test-Command "List Columns (/columns)" @("/columns") @(
        "Columns",
        "Column",
        "Type",
        "Nulls",
        "Unique",
        "CustomerId",
        "CreditScore",
        "Numeric"
    )

    # Test 11: Show column details
    Test-Command "Column Details (/column Age)" @("/column Age") @(
        "Column: Age",
        "Type:",
        "Numeric",
        "Mean:",
        "Median:",
        "Range:"
    )

    # Test 12: Invalid column
    Test-Command "Invalid Column (/column NonExistent)" @("/column NonExistent") @(
        "Column not found",
        "/columns"
    )

    # Test 13: Show alerts
    Test-Command "Show Alerts (/alerts)" @("/alerts") @(
        "Alerts",
        "Warning|Error|Info"
    )

    # Test 14: Show insights
    Test-Command "Show Insights (/insights)" @("/insights") @(
        "Insights",
        "score:"
    )

    # Test 15: Toggle verbose
    Test-Command "Toggle Verbose (/verbose)" @("/verbose") @(
        "Verbose mode:",
        "on|off"
    )

    # Test 16: Unknown command
    Test-Command "Unknown Command (/unknown)" @("/unknown") @(
        "Unknown command",
        "/"
    )

    # Test 17: Multiple commands in sequence
    Test-Command "Command Sequence" @("/status", "/columns", "/profile Brief", "/status") @(
        "Session Status",
        "Columns",
        "Switched to 'Brief'",
        "Brief"
    )

    # Summary
    Write-Host "`n$('=' * 60)" -ForegroundColor Yellow
    Write-Host "TEST SUMMARY" -ForegroundColor Yellow
    Write-Host "$('=' * 60)" -ForegroundColor Yellow
    Write-Host "Passed: $script:TestsPassed" -ForegroundColor Green
    Write-Host "Failed: $script:TestsFailed" -ForegroundColor $(if ($script:TestsFailed -gt 0) { "Red" } else { "Green" })
    Write-Host "Total:  $($script:TestsPassed + $script:TestsFailed)" -ForegroundColor Yellow

    if ($script:TestsFailed -gt 0) {
        exit 1
    }
    exit 0
}
finally {
    Pop-Location
}
