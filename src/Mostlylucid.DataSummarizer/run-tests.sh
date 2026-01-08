#!/bin/bash
# DataSummarizer Comprehensive Test Script
# Run from Mostlylucid.DataSummarizer directory

set -e

TEST_DIR="test-output"
VECTOR_DB="$TEST_DIR/test-registry.duckdb"
PASSED=0
FAILED=0

# Colors
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
NC='\033[0m' # No Color

pass() { echo -e "${GREEN}[PASS]${NC} $1"; ((PASSED++)); }
fail() { echo -e "${RED}[FAIL]${NC} $1"; ((FAILED++)); }
info() { echo -e "${CYAN}[INFO]${NC} $1"; }
test_header() { echo -e "\n${YELLOW}=== $1 ===${NC}"; }

# Setup
info "Setting up test environment..."
rm -rf "$TEST_DIR"
mkdir -p "$TEST_DIR"

# Build
test_header "Building project"
if dotnet build --no-restore -q; then
    pass "Build succeeded"
else
    fail "Build failed"
    exit 1
fi

# ============================================================================
# TEST 1: Profile Bank Churn CSV
# ============================================================================
test_header "TEST 1: Profile Bank_Churn.csv"
OUTPUT=$(dotnet run -- -f "sampledata/Bank+Customer+Churn/Bank_Churn.csv" --no-llm 2>&1 | cat -v)

if echo "$OUTPUT" | grep -q "10,000 rows"; then
    pass "Row count correct (10,000)"
else
    fail "Row count incorrect"
fi

if echo "$OUTPUT" | grep -q "13 columns"; then
    pass "Column count correct (13)"
else
    fail "Column count incorrect"
fi

if echo "$OUTPUT" | grep -q "CreditScore"; then
    pass "CreditScore column found"
else
    fail "CreditScore not found"
fi

if echo "$OUTPUT" | grep -q "outliers"; then
    pass "Outlier detection working"
else
    fail "Outlier detection not working"
fi

# ============================================================================
# TEST 2: Profile CO2 Emissions
# ============================================================================
test_header "TEST 2: Profile CO2 Emissions"
OUTPUT=$(dotnet run -- -f "sampledata/CO2+Emissions/visualizing_global_co2_data.csv" --no-llm 2>&1 | cat -v)

if echo "$OUTPUT" | grep -q "rows"; then
    pass "CO2 data profiled"
else
    fail "CO2 profiling failed"
fi

# ============================================================================
# TEST 3: Profile Hospital Patients
# ============================================================================
test_header "TEST 3: Profile Hospital Patients"
OUTPUT=$(dotnet run -- -f "sampledata/Hospital+Patient+Records/patients.csv" --no-llm 2>&1 | cat -v)

if echo "$OUTPUT" | grep -q "rows"; then
    pass "Hospital patients profiled"
else
    fail "Hospital patients profiling failed"
fi

# ============================================================================
# TEST 4: Profile MoMA Artworks
# ============================================================================
test_header "TEST 4: Profile MoMA Artworks"
OUTPUT=$(dotnet run -- -f "sampledata/MoMA+Art+Collection/Artworks.csv" --no-llm 2>&1 | cat -v)

if echo "$OUTPUT" | grep -q "rows"; then
    pass "MoMA Artworks profiled"
else
    fail "MoMA Artworks profiling failed"
fi

# ============================================================================
# TEST 5: Profile Wine Reviews (130k rows)
# ============================================================================
test_header "TEST 5: Profile Wine Reviews (130k rows)"
START_TIME=$(date +%s)
OUTPUT=$(dotnet run -- -f "sampledata/winemag-data-130k-v2.csv/winemag-data-130k-v2.csv" --no-llm 2>&1 | cat -v)
END_TIME=$(date +%s)
ELAPSED=$((END_TIME - START_TIME))

if echo "$OUTPUT" | grep -q "rows"; then
    pass "Wine reviews profiled in ${ELAPSED}s"
else
    fail "Wine reviews profiling failed"
fi

if [ "$ELAPSED" -lt 60 ]; then
    pass "Performance acceptable (<60s for 130k rows)"
else
    fail "Performance too slow (>${ELAPSED}s)"
fi

# ============================================================================
# TEST 6: Excel file support
# ============================================================================
test_header "TEST 6: Excel file support"
OUTPUT=$(dotnet run -- -f "sampledata/Bank+Customer+Churn/Bank_Churn_Messy.xlsx" --no-llm 2>&1 | cat -v)

if echo "$OUTPUT" | grep -q "rows"; then
    pass "Excel file profiled"
else
    fail "Excel profiling failed"
    echo "$OUTPUT" | head -20
fi

# ============================================================================
# TEST 7: Profile subcommand with JSON output
# ============================================================================
test_header "TEST 7: Profile subcommand"
dotnet run -- profile --file "sampledata/Bank+Customer+Churn/Bank_Churn.csv" --output "$TEST_DIR/bank.profile.json" 2>&1 > /dev/null

if [ -f "$TEST_DIR/bank.profile.json" ]; then
    pass "Profile JSON created"
    
    # Check JSON content
    if grep -q '"RowCount": 10000' "$TEST_DIR/bank.profile.json"; then
        pass "Profile JSON has correct row count"
    else
        fail "Profile JSON row count wrong"
    fi
else
    fail "Profile JSON not created"
fi

# ============================================================================
# TEST 8: Synth subcommand
# ============================================================================
test_header "TEST 8: Synth subcommand"
dotnet run -- synth --profile "$TEST_DIR/bank.profile.json" --synthesize-to "$TEST_DIR/synthetic.csv" --synthesize-rows 500 2>&1 > /dev/null

if [ -f "$TEST_DIR/synthetic.csv" ]; then
    ROW_COUNT=$(wc -l < "$TEST_DIR/synthetic.csv")
    ROW_COUNT=$((ROW_COUNT - 1)) # minus header
    
    if [ "$ROW_COUNT" -eq 500 ]; then
        pass "Synthetic CSV has correct row count (500)"
    else
        fail "Synthetic CSV row count wrong: $ROW_COUNT"
    fi
    
    # Check header
    if head -1 "$TEST_DIR/synthetic.csv" | grep -q "CustomerId"; then
        pass "Synthetic CSV has correct columns"
    else
        fail "Synthetic CSV columns wrong"
    fi
else
    fail "Synthetic CSV not created"
fi

# ============================================================================
# TEST 9: Validate subcommand
# ============================================================================
test_header "TEST 9: Validate subcommand"
dotnet run -- validate --source "sampledata/Bank+Customer+Churn/Bank_Churn.csv" --target "$TEST_DIR/synthetic.csv" --output "$TEST_DIR/validation.json" 2>&1 > /dev/null

if [ -f "$TEST_DIR/validation.json" ]; then
    pass "Validation JSON created"
    
    if grep -q '"DriftScore"' "$TEST_DIR/validation.json"; then
        pass "Validation has DriftScore"
    else
        fail "Validation missing DriftScore"
    fi
else
    fail "Validation JSON not created"
fi

# ============================================================================
# TEST 10: Ingest multiple files
# ============================================================================
test_header "TEST 10: Ingest files into registry"
OUTPUT=$(dotnet run -- --ingest-dir "sampledata/Bank+Customer+Churn" --no-llm --vector-db "$VECTOR_DB" 2>&1 | cat -v)

if echo "$OUTPUT" | grep -q "Ingest"; then
    pass "Ingest started"
else
    fail "Ingest failed"
fi

# ============================================================================
# TEST 11: Registry query
# ============================================================================
test_header "TEST 11: Registry query"
OUTPUT=$(dotnet run -- --registry-query "What columns relate to customers?" --vector-db "$VECTOR_DB" --no-llm 2>&1 | cat -v)

if echo "$OUTPUT" | grep -qi "context\|answer\|registry"; then
    pass "Registry query returned results"
else
    fail "Registry query failed"
    echo "$OUTPUT" | head -20
fi

# ============================================================================
# TEST 12: Small CSV edge case
# ============================================================================
test_header "TEST 12: Small CSV edge case"
echo -e "A,B\n1,2" > "$TEST_DIR/tiny.csv"
OUTPUT=$(dotnet run -- -f "$TEST_DIR/tiny.csv" --no-llm 2>&1 | cat -v)

if echo "$OUTPUT" | grep -q "1 row"; then
    pass "Tiny CSV handled correctly"
else
    fail "Tiny CSV handling failed"
fi

# ============================================================================
# TEST 13: Global Electronics Sales
# ============================================================================
test_header "TEST 13: Global Electronics Sales"
OUTPUT=$(dotnet run -- -f "sampledata/Global+Electronics+Retailer/Sales.csv" --no-llm 2>&1 | cat -v)

if echo "$OUTPUT" | grep -q "rows"; then
    pass "Sales CSV profiled"
else
    fail "Sales CSV profiling failed"
fi

# ============================================================================
# TEST 14: UFO Sightings
# ============================================================================
test_header "TEST 14: UFO Sightings"
OUTPUT=$(dotnet run -- -f "sampledata/ufo_sightings_scrubbed.csv/ufo_sightings_scrubbed.csv" --no-llm 2>&1 | cat -v)

if echo "$OUTPUT" | grep -q "rows"; then
    pass "UFO sightings profiled"
else
    fail "UFO sightings profiling failed"
fi

# ============================================================================
# SUMMARY
# ============================================================================
echo ""
echo "========================================"
echo "TEST SUMMARY"
echo "========================================"
echo -e "${GREEN}Passed: $PASSED${NC}"
echo -e "${RED}Failed: $FAILED${NC}"

if [ "$FAILED" -eq 0 ]; then
    echo -e "\n${GREEN}All tests passed!${NC}"
    exit 0
else
    echo -e "\n${RED}Some tests failed!${NC}"
    exit 1
fi
