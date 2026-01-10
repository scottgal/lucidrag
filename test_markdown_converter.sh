#!/bin/bash
# Test script for markdown table converter

echo "=== Markdown Table Converter Test ==="
echo ""

# Build DataSummarizer
echo "[1] Building DataSummarizer..."
cd /e/source/lucidrag/src/Mostlylucid.DataSummarizer
dotnet build -c Release > /dev/null 2>&1

if [ $? -ne 0 ]; then
    echo "❌ Build failed"
    exit 1
fi
echo "✓ Build succeeded"
echo ""

# Test 1: List tables only
echo "[2] Listing tables in test file..."
dotnet run -- convert-markdown -i /e/source/lucidrag/test_markdown_tables.md --list-only

echo ""
echo "---"
echo ""

# Test 2: Convert tables
echo "[3] Converting tables to CSV..."
dotnet run -- convert-markdown -i /e/source/lucidrag/test_markdown_tables.md -d /e/source/lucidrag/converted_tables -v

echo ""
echo "---"
echo ""

# Test 3: Show converted files
echo "[4] Converted files:"
if [ -d "/e/source/lucidrag/converted_tables" ]; then
    ls -lh /e/source/lucidrag/converted_tables/*.csv 2>/dev/null | awk '{print $9, "(" $5 ")"}'
fi

echo ""
echo "---"
echo ""

# Test 4: Preview first table
echo "[5] Preview of first converted table:"
if [ -f "/e/source/lucidrag/converted_tables/test_markdown_tables_table_1.csv" ]; then
    head -5 /e/source/lucidrag/converted_tables/test_markdown_tables_table_1.csv
fi

echo ""
echo "---"
echo ""

# Test 5: Profile one of the tables
echo "[6] Profiling Table 7 (Numeric Data)..."
TABLE_FILE="/e/source/lucidrag/converted_tables/test_markdown_tables_table_7.csv"
if [ -f "$TABLE_FILE" ]; then
    dotnet run -- profile -f "$TABLE_FILE" --fast --no-report
fi

echo ""
echo "=== Test Complete ==="
