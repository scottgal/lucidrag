#!/bin/bash
# Comprehensive test script for DataSummarizer
# Tests all major features against sample data

set -e  # Exit on error

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

# Counters
TESTS_RUN=0
TESTS_PASSED=0
TESTS_FAILED=0

# Find the binary
if [ -f "bin/Release/net10.0/datasummarizer.exe" ]; then
    DS="bin/Release/net10.0/datasummarizer.exe"
elif [ -f "bin/Release/net10.0/datasummarizer" ]; then
    DS="bin/Release/net10.0/datasummarizer"
elif [ -f "bin/Debug/net10.0/datasummarizer.exe" ]; then
    DS="bin/Debug/net10.0/datasummarizer.exe"
elif [ -f "bin/Debug/net10.0/datasummarizer" ]; then
    DS="bin/Debug/net10.0/datasummarizer"
else
    echo -e "${RED}❌ datasummarizer binary not found. Run 'dotnet build -c Release' first.${NC}"
    exit 1
fi

echo -e "${BLUE}Using binary: $DS${NC}\n"

# Test data files
TEST_CSV="../pii-test.csv"
TEST_TIMESERIES="../timeseries-weekly.csv"

# Create test output directory
mkdir -p test-outputs
cd test-outputs

# Helper functions
run_test() {
    local test_name="$1"
    local command="$2"
    local expected_pattern="$3"
    
    TESTS_RUN=$((TESTS_RUN + 1))
    echo -e "${YELLOW}[TEST $TESTS_RUN] $test_name${NC}"
    echo "Command: $command"
    
    if eval "$command" > test_output.tmp 2>&1; then
        if [ -z "$expected_pattern" ] || grep -q "$expected_pattern" test_output.tmp; then
            echo -e "${GREEN}✓ PASSED${NC}\n"
            TESTS_PASSED=$((TESTS_PASSED + 1))
            return 0
        else
            echo -e "${RED}✗ FAILED - Expected pattern not found: $expected_pattern${NC}"
            echo "Output:"
            cat test_output.tmp
            echo -e "\n"
            TESTS_FAILED=$((TESTS_FAILED + 1))
            return 1
        fi
    else
        echo -e "${RED}✗ FAILED - Command exited with error${NC}"
        echo "Output:"
        cat test_output.tmp
        echo -e "\n"
        TESTS_FAILED=$((TESTS_FAILED + 1))
        return 1
    fi
}

run_test_expect_fail() {
    local test_name="$1"
    local command="$2"
    
    TESTS_RUN=$((TESTS_RUN + 1))
    echo -e "${YELLOW}[TEST $TESTS_RUN] $test_name${NC}"
    echo "Command: $command"
    
    # Disable set -e for this block to properly catch expected failures
    set +e
    eval "$command" > test_output.tmp 2>&1
    local exit_code=$?
    set -e
    
    if [ $exit_code -eq 0 ]; then
        echo -e "${RED}✗ FAILED - Command should have failed but succeeded${NC}"
        cat test_output.tmp
        echo -e "\n"
        TESTS_FAILED=$((TESTS_FAILED + 1))
        return 1
    else
        echo -e "${GREEN}✓ PASSED (expected failure, exit code: $exit_code)${NC}\n"
        TESTS_PASSED=$((TESTS_PASSED + 1))
        return 0
    fi
}

echo -e "${BLUE}═══════════════════════════════════════════════════════════════${NC}"
echo -e "${BLUE}  DataSummarizer Comprehensive Test Suite${NC}"
echo -e "${BLUE}═══════════════════════════════════════════════════════════════${NC}\n"

# ============================================================================
# BASIC PROFILING TESTS
# ============================================================================
echo -e "${BLUE}▶ Basic Profiling Tests${NC}\n"

run_test "Basic profile (no LLM, fast mode)" \
    "../$DS -f ../$TEST_CSV --no-llm --fast" \
    "rows"

run_test "Full profile (no LLM)" \
    "../$DS -f ../$TEST_CSV --no-llm" \
    "Summary"

run_test "Profile with output file" \
    "../$DS -f ../$TEST_CSV --no-llm -o basic-profile.txt" \
    ""

run_test "Verbose output" \
    "../$DS -f ../$TEST_CSV --no-llm --fast --verbose" \
    "Profiling"

# ============================================================================
# PROFILE COMMAND (JSON OUTPUT)
# ============================================================================
echo -e "${BLUE}▶ Profile Command Tests${NC}\n"

run_test "Profile command - save JSON" \
    "../$DS profile -f ../$TEST_CSV --output pii-profile.json --no-llm" \
    ""

run_test "Verify profile JSON exists and is valid" \
    "test -f pii-profile.json && grep -q '\"RowCount\"' pii-profile.json" \
    ""

# ============================================================================
# TOOL COMMAND (COMPACT JSON FOR AGENTS)
# ============================================================================
echo -e "${BLUE}▶ Tool Command Tests${NC}\n"

run_test "Tool command - basic JSON output" \
    "../$DS tool -f ../$TEST_CSV > tool-output.json" \
    ""

run_test "Tool command - with store" \
    "../$DS tool -f ../$TEST_CSV --store > tool-store.json" \
    ""

run_test "Tool command - fast mode" \
    "../$DS tool -f ../$TEST_CSV --fast --skip-correlations > tool-fast.json" \
    ""

run_test "Tool command - compact output" \
    "../$DS tool -f ../$TEST_CSV --compact > tool-compact.json" \
    ""

run_test "Tool command - auto-drift (should have no drift - first profile)" \
    "../$DS tool -f ../$TEST_CSV --auto-drift --store > tool-drift.json" \
    ""

# ============================================================================
# SYNTH COMMAND (SYNTHETIC DATA)
# ============================================================================
echo -e "${BLUE}▶ Synthetic Data Generation Tests${NC}\n"

run_test "Synth - generate 100 rows" \
    "../$DS synth --profile pii-profile.json --synthesize-to synthetic-100.csv --synthesize-rows 100 --verbose" \
    ""

run_test "Verify synthetic CSV exists" \
    "test -f synthetic-100.csv && wc -l synthetic-100.csv" \
    ""

run_test "Synth - generate 1000 rows" \
    "../$DS synth --profile pii-profile.json --synthesize-to synthetic-1000.csv --synthesize-rows 1000" \
    ""

# ============================================================================
# VALIDATE COMMAND (DRIFT & CONSTRAINTS)
# ============================================================================
echo -e "${BLUE}▶ Validation Tests${NC}\n"

run_test "Validate - basic drift comparison" \
    "../$DS validate --source ../$TEST_CSV --target synthetic-100.csv --no-llm --format json > validation.json" \
    ""

run_test "Validate - generate constraints from source" \
    "../$DS validate --source ../$TEST_CSV --target ../$TEST_CSV --generate-constraints --no-llm" \
    "Generated"

run_test "Verify constraints JSON exists" \
    "test -f ../../pii-test.constraints.json && grep -q '\"Constraints\"' ../../pii-test.constraints.json" \
    ""

run_test "Validate - check constraints (should pass)" \
    "../$DS validate --source ../$TEST_CSV --target ../$TEST_CSV --constraints ../../pii-test.constraints.json --format markdown --no-llm > constraint-validation.md" \
    ""

run_test "Validate - markdown format" \
    "../$DS validate --source ../$TEST_CSV --target synthetic-100.csv --format markdown --no-llm > validation.md" \
    ""

run_test "Validate - HTML format" \
    "../$DS validate --source ../$TEST_CSV --target synthetic-100.csv --format html --no-llm > validation.html" \
    ""

# Test strict mode - should FAIL because different schemas
run_test_expect_fail "Validate - strict mode with different data (should fail)" \
    "../$DS validate --source ../$TEST_CSV --target ../$TEST_TIMESERIES --constraints ../../pii-test.constraints.json --strict --no-llm"

# ============================================================================
# SEGMENT COMPARISON
# ============================================================================
echo -e "${BLUE}▶ Segment Comparison Tests${NC}\n"

run_test "Segment - compare two files (JSON)" \
    "../$DS segment --segment-a ../$TEST_CSV --segment-b synthetic-100.csv --format json > segment-comparison.json" \
    ""

run_test "Segment - compare with custom names (markdown)" \
    "../$DS segment --segment-a ../$TEST_CSV --segment-b synthetic-100.csv --name-a \"Original\" --name-b \"Synthetic\" --format markdown > segment-comparison.md" \
    ""

run_test "Segment - different datasets" \
    "../$DS segment --segment-a ../$TEST_CSV --segment-b ../$TEST_TIMESERIES --format json > segment-different.json" \
    ""

# ============================================================================
# STORE MANAGEMENT
# ============================================================================
echo -e "${BLUE}▶ Profile Store Management Tests${NC}\n"

# Store a few profiles
run_test "Store - profile 1" \
    "../$DS tool -f ../$TEST_CSV --store --store-path store-test.duckdb > /dev/null" \
    ""

run_test "Store - profile 2 (synthetic)" \
    "../$DS tool -f synthetic-100.csv --store --store-path store-test.duckdb > /dev/null" \
    ""

run_test "Store - profile 3 (timeseries)" \
    "../$DS tool -f ../$TEST_TIMESERIES --store --store-path store-test.duckdb > /dev/null" \
    ""

run_test "Store list - show all profiles" \
    "../$DS store list --store-path store-test.duckdb" \
    "ID"

run_test "Store stats - show statistics" \
    "../$DS store stats --store-path store-test.duckdb" \
    "Total profiles"

run_test "Store prune - keep 2 per schema" \
    "../$DS store prune --keep 2 --store-path store-test.duckdb" \
    ""

run_test "Store list after prune" \
    "../$DS store list --store-path store-test.duckdb" \
    "ID"

# ============================================================================
# PERFORMANCE OPTIONS
# ============================================================================
echo -e "${BLUE}▶ Performance Options Tests${NC}\n"

run_test "Column selection - specific columns" \
    "../$DS -f ../$TEST_CSV --columns Name,Email --no-llm --fast" \
    "Name"

run_test "Column exclusion" \
    "../$DS -f ../$TEST_CSV --exclude-columns SSN --no-llm --fast" \
    "Email"

run_test "Max columns limit" \
    "../$DS -f ../$TEST_TIMESERIES --max-columns 3 --no-llm --fast" \
    "columns"

run_test "Fast mode with skip correlations" \
    "../$DS -f ../$TEST_TIMESERIES --fast --skip-correlations --no-llm" \
    "Summary"

# ============================================================================
# OUTPUT FORMATS
# ============================================================================
echo -e "${BLUE}▶ Output Format Tests${NC}\n"

run_test "Tool output - JSON format (default)" \
    "../$DS tool -f ../$TEST_CSV --format json > format-json.json" \
    ""

run_test "Tool output - Markdown format" \
    "../$DS tool -f ../$TEST_CSV --format markdown > format-markdown.md" \
    ""

run_test "Tool output - HTML format" \
    "../$DS tool -f ../$TEST_CSV --format html > format-html.html" \
    ""

# ============================================================================
# ERROR HANDLING
# ============================================================================
echo -e "${BLUE}▶ Error Handling Tests${NC}\n"

run_test_expect_fail "Invalid file path" \
    "../$DS -f nonexistent.csv --no-llm"

run_test_expect_fail "Invalid command" \
    "../$DS invalidcommand -f ../$TEST_CSV --no-llm"

run_test_expect_fail "Missing required option" \
    "../$DS synth --synthesize-to output.csv"

# ============================================================================
# SUMMARY
# ============================================================================
echo -e "\n${BLUE}═══════════════════════════════════════════════════════════════${NC}"
echo -e "${BLUE}  Test Summary${NC}"
echo -e "${BLUE}═══════════════════════════════════════════════════════════════${NC}\n"

echo -e "Total tests run: ${BLUE}$TESTS_RUN${NC}"
echo -e "Passed: ${GREEN}$TESTS_PASSED${NC}"
echo -e "Failed: ${RED}$TESTS_FAILED${NC}"

if [ $TESTS_FAILED -eq 0 ]; then
    echo -e "\n${GREEN}✓ All tests passed!${NC}\n"
    exit 0
else
    echo -e "\n${RED}✗ Some tests failed.${NC}\n"
    exit 1
fi
