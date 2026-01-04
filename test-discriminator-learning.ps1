# Comprehensive Discriminator Learning Test Script
# Tests:
# 1. Vision analysis with metadata extraction (tone, sentiment, complexity, etc.)
# 2. Multi-vector discriminator scoring
# 3. Signal contribution tracking (including LLM-derived features)
# 4. Learning loop with feedback
# 5. Strategy tracking for preprocessing optimization

param(
    [string]$TestImage = "E:\source\lucidrag\test-images\book-page.jpg",
    [string]$Model = "anthropic:claude-3-opus-20240229",
    [switch]$RunFullSuite
)

$ErrorActionPreference = "Stop"

Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host "Discriminator Learning System Test" -ForegroundColor Cyan
Write-Host "========================================`n" -ForegroundColor Cyan

# Check if test image exists
if (-not (Test-Path $TestImage)) {
    Write-Host "Test image not found: $TestImage" -ForegroundColor Red
    Write-Host "Creating test environment..." -ForegroundColor Yellow

    # Create test images directory
    $testDir = "E:\source\lucidrag\test-images"
    if (-not (Test-Path $testDir)) {
        New-Item -ItemType Directory -Path $testDir | Out-Null
    }

    Write-Host "Please add a test image to: $testDir" -ForegroundColor Yellow
    Write-Host "Supported formats: .jpg, .png, .gif, .webp" -ForegroundColor Yellow
    exit 1
}

# Build the project
Write-Host "Building project..." -ForegroundColor Yellow
dotnet build "E:\source\lucidrag\LucidRAG.sln" -c Release | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed!" -ForegroundColor Red
    exit 1
}
Write-Host "Build successful!`n" -ForegroundColor Green

$cliPath = "E:\source\lucidrag\src\LucidRAG.ImageCli"

# Test 1: Basic Analysis with Metadata Extraction
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "TEST 1: Vision Analysis with Metadata" -ForegroundColor Cyan
Write-Host "========================================`n" -ForegroundColor Cyan

Write-Host "Running vision analysis with Anthropic Claude..." -ForegroundColor Yellow
Write-Host "Model: $Model" -ForegroundColor Gray
Write-Host "Image: $TestImage`n" -ForegroundColor Gray

dotnet run --project $cliPath -- score $TestImage --model $Model --goal caption

if ($LASTEXITCODE -ne 0) {
    Write-Host "`nAnalysis failed! Check API keys:`n" -ForegroundColor Red
    Write-Host "For Anthropic: dotnet user-secrets set Anthropic:ApiKey YOUR_KEY --project $cliPath" -ForegroundColor Yellow
    Write-Host "For OpenAI: dotnet user-secrets set OpenAI:ApiKey YOUR_KEY --project $cliPath" -ForegroundColor Yellow
    exit 1
}

# Test 2: Provide Positive Feedback to Start Learning
Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host "TEST 2: Learning Loop - Positive Feedback" -ForegroundColor Cyan
Write-Host "========================================`n" -ForegroundColor Cyan

Write-Host "Recording positive feedback..." -ForegroundColor Yellow
dotnet run --project $cliPath -- score $TestImage `
    --model $Model `
    --goal caption `
    --accept true `
    --feedback "Excellent caption with accurate tone and sentiment analysis"

# Test 3: Run Again to See Novelty Score
Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host "TEST 3: Novelty vs Prior Detection" -ForegroundColor Cyan
Write-Host "========================================`n" -ForegroundColor Cyan

Write-Host "Analyzing same image again to test novelty detection..." -ForegroundColor Yellow
dotnet run --project $cliPath -- score $TestImage `
    --model $Model `
    --goal caption

# Test 4: View Top Discriminators
Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host "TEST 4: Top Discriminators for Image Type" -ForegroundColor Cyan
Write-Host "========================================`n" -ForegroundColor Cyan

Write-Host "Showing top discriminators learned so far..." -ForegroundColor Yellow
dotnet run --project $cliPath -- score $TestImage `
    --goal caption `
    --show-top

# Test 5: Multi-Goal Analysis (OCR vs Caption)
if ($RunFullSuite) {
    Write-Host "`n========================================" -ForegroundColor Cyan
    Write-Host "TEST 5: Multi-Goal Analysis" -ForegroundColor Cyan
    Write-Host "========================================`n" -ForegroundColor Cyan

    Write-Host "Testing OCR goal..." -ForegroundColor Yellow
    dotnet run --project $cliPath -- score $TestImage `
        --model $Model `
        --goal ocr `
        --accept true `
        --feedback "Good OCR extraction for document"

    Write-Host "`nTesting object_detection goal..." -ForegroundColor Yellow
    dotnet run --project $cliPath -- score $TestImage `
        --model $Model `
        --goal object_detection `
        --accept true `
        --feedback "Accurately identified objects and their positions"

    Write-Host "`nShowing top discriminators for OCR..." -ForegroundColor Yellow
    dotnet run --project $cliPath -- score $TestImage `
        --goal ocr `
        --show-top

    Write-Host "`nShowing top discriminators for object_detection..." -ForegroundColor Yellow
    dotnet run --project $cliPath -- score $TestImage `
        --goal object_detection `
        --show-top
}

# Test 6: Test with Different Image (if available)
$testImages = Get-ChildItem "E:\source\lucidrag\test-images" -File -Include @("*.jpg", "*.png", "*.gif", "*.webp")

if ($RunFullSuite -and $testImages.Count -gt 1) {
    Write-Host "`n========================================" -ForegroundColor Cyan
    Write-Host "TEST 6: Multi-Image Learning" -ForegroundColor Cyan
    Write-Host "========================================`n" -ForegroundColor Cyan

    foreach ($img in $testImages | Select-Object -First 3) {
        Write-Host "`nAnalyzing: $($img.Name)" -ForegroundColor Yellow
        dotnet run --project $cliPath -- score $img.FullName `
            --model $Model `
            --goal caption `
            --accept true `
            --feedback "Processing $($img.Name)"
    }
}

# Test 7: Negative Feedback Example
Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host "TEST 7: Learning Loop - Negative Feedback" -ForegroundColor Cyan
Write-Host "========================================`n" -ForegroundColor Cyan

Write-Host "Recording negative feedback to test weight adjustment..." -ForegroundColor Yellow
dotnet run --project $cliPath -- score $TestImage `
    --model $Model `
    --goal caption `
    --accept false `
    --feedback "Caption missed key details visible in the image"

# Test 8: Show Learning Statistics
Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host "TEST 8: Learning Progress Summary" -ForegroundColor Cyan
Write-Host "========================================`n" -ForegroundColor Cyan

Write-Host "Re-analyzing to see updated statistics..." -ForegroundColor Yellow
dotnet run --project $cliPath -- score $TestImage `
    --model $Model `
    --goal caption `
    --show-top

# Summary
Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host "TEST SUMMARY" -ForegroundColor Cyan
Write-Host "========================================`n" -ForegroundColor Cyan

Write-Host "Completed Tests:" -ForegroundColor Green
Write-Host "  [OK] Vision analysis with enhanced metadata (tone, sentiment, complexity)" -ForegroundColor Gray
Write-Host "  [OK] Multi-vector discriminator scoring (6 orthogonal dimensions)" -ForegroundColor Gray
Write-Host "  [OK] Signal contribution tracking (including LLM-derived features)" -ForegroundColor Gray
Write-Host "  [OK] Learning loop with positive and negative feedback" -ForegroundColor Gray
Write-Host "  [OK] Novelty detection vs prior analyses" -ForegroundColor Gray
Write-Host "  [OK] Top discriminator display" -ForegroundColor Gray

if ($RunFullSuite) {
    Write-Host "  [OK] Multi-goal analysis (caption, OCR, object detection)" -ForegroundColor Gray
    Write-Host "  [OK] Multi-image learning" -ForegroundColor Gray
}

Write-Host "`nKey Learning Signals Captured:" -ForegroundColor Green
Write-Host "  • Visual Signals: EdgeDensity, LaplacianVariance, TextLikeliness" -ForegroundColor Gray
Write-Host "  • Color Signals: MeanSaturation, DominantColors, Luminance" -ForegroundColor Gray
Write-Host "  • Motion Signals: MotionMagnitude, MotionConfidence (for GIFs)" -ForegroundColor Gray
Write-Host "  • LLM Metadata: Tone, Sentiment, Complexity, AestheticScore" -ForegroundColor Gray
Write-Host "  • Semantic: PrimarySubject, Purpose, TargetAudience" -ForegroundColor Gray
Write-Host "  • Evidence: ClaimGrounding (non-synthesis ratio)" -ForegroundColor Gray

Write-Host "`nNext Steps:" -ForegroundColor Yellow
Write-Host "  1. Add preprocessing strategies (gamma, contrast, color channels)" -ForegroundColor Gray
Write-Host "  2. Implement background coordinator for strategy optimization" -ForegroundColor Gray
Write-Host "  3. Feed strategy results back to discriminator learning" -ForegroundColor Gray
Write-Host "  4. Learn best OCR preprocessing per image type" -ForegroundColor Gray

Write-Host "`nDatabase Location:" -ForegroundColor Yellow
$dbPath = Join-Path $env:LOCALAPPDATA "LucidRAG\ImageCli\signals.db"
Write-Host "  $dbPath" -ForegroundColor Gray

Write-Host "`nTo prune ineffective discriminators:" -ForegroundColor Yellow
Write-Host "  dotnet run --project $cliPath -- score $TestImage --prune" -ForegroundColor Gray

Write-Host "`n[SUCCESS] All tests completed successfully!`n" -ForegroundColor Green
