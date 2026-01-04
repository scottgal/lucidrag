# LucidRAG Image CLI - Natural Language Query Demo
# Demonstrates forensics pipeline, OCR, and conversational filtering

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "LucidRAG Image CLI Demo" -ForegroundColor Cyan
Write-Host "Natural Language Queries + Forensics" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Check if Ollama is running
Write-Host "Checking Ollama availability..." -ForegroundColor Yellow
try {
    $response = Invoke-WebRequest -Uri "http://localhost:11434/api/tags" -Method GET -TimeoutSec 2
    Write-Host "✓ Ollama is running" -ForegroundColor Green
} catch {
    Write-Host "✗ Ollama is not running. Vision LLM features will be disabled." -ForegroundColor Red
    Write-Host "  Start Ollama with: ollama serve" -ForegroundColor Yellow
}

Write-Host ""

# Demo 1: Console Pixel Art Preview
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Demo 1: Console Pixel Art Preview" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

Write-Host "Rendering a test image in console with different modes..." -ForegroundColor Yellow
Write-Host ""

# Use actual test images from the test directory
$testImagesDir = "E:\source\lucidrag\src\Mostlylucid.DocSummarizer.Images.Tests\TestImages"
$testImage = "$testImagesDir\01-home.png"

if (Test-Path $testImage) {
    Write-Host "Preview modes available:" -ForegroundColor Green
    Write-Host "  1. ColorBlocks (best quality, full color)" -ForegroundColor White
    Write-Host "  2. GrayscaleBlocks (good for B&W)" -ForegroundColor White
    Write-Host "  3. Ascii (classic ASCII art)" -ForegroundColor White
    Write-Host "  4. Braille (highest resolution)" -ForegroundColor White
    Write-Host ""

    Write-Host "Rendering with ColorBlocks mode:" -ForegroundColor Yellow
    dotnet run --project "E:\source\lucidrag\src\LucidRAG.ImageCli\LucidRAG.ImageCli.csproj" -- preview "$testImage" --mode ColorBlocks --width 60 --height 30

    Write-Host ""
    Write-Host "Compact preview (single line):" -ForegroundColor Yellow
    dotnet run --project "E:\source\lucidrag\src\LucidRAG.ImageCli\LucidRAG.ImageCli.csproj" -- preview "$testImage" --compact --width 40
} else {
    Write-Host "Test image not found. Skipping preview demo." -ForegroundColor Red
}

Write-Host ""
Write-Host ""

# Demo 2: Single Image Analysis with Forensics
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Demo 2: Forensic Analysis" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

Write-Host "Analyzing image with full forensics pipeline:" -ForegroundColor Yellow
Write-Host "  - EXIF metadata extraction" -ForegroundColor White
Write-Host "  - Digital fingerprinting (PDQ, color histogram)" -ForegroundColor White
Write-Host "  - Error Level Analysis (ELA) for tampering" -ForegroundColor White
Write-Host "  - OCR with bounding boxes" -ForegroundColor White
Write-Host "  - Vision LLM verification (if available)" -ForegroundColor White
Write-Host ""

if (Test-Path $testImage) {
    dotnet run --project "E:\source\lucidrag\src\LucidRAG.ImageCli\LucidRAG.ImageCli.csproj" -- analyze "$testImage" --format table --include-ocr --use-llm
} else {
    Write-Host "Test image not found. Skipping analysis demo." -ForegroundColor Red
}

Write-Host ""
Write-Host ""

# Demo 3: Natural Language Queries
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Demo 3: Natural Language Queries" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

Write-Host "Example queries:" -ForegroundColor Green
Write-Host '  "Show me all images with a sunset and the sea"' -ForegroundColor White
Write-Host '  "Find images with green as predominant color that are abstract"' -ForegroundColor White
Write-Host '  "Screenshots with text in them"' -ForegroundColor White
Write-Host '  "High resolution photos with people"' -ForegroundColor White
Write-Host ""

# Use test images directory for demo
if (Test-Path $testImagesDir) {
    Write-Host "Searching test images folder with natural language query..." -ForegroundColor Yellow
    Write-Host 'Query: "screenshots with interface elements"' -ForegroundColor Cyan
    Write-Host ""

    dotnet run --project "E:\source\lucidrag\src\LucidRAG.ImageCli\LucidRAG.ImageCli.csproj" -- batch "$testImagesDir" --query "screenshots with interface elements" --max-parallel 4 --format table
} else {
    Write-Host "Test images folder not found. Skipping query demo." -ForegroundColor Red
}

Write-Host ""
Write-Host ""

# Demo 4: Batch Processing with Filters
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Demo 4: Batch Processing" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

Write-Host "Processing folder with filters:" -ForegroundColor Yellow
Write-Host "  - Pattern: **/*.jpg (glob filter)" -ForegroundColor White
Write-Host "  - Type filter: Photo" -ForegroundColor White
Write-Host "  - Min text score: 0.0 (all images)" -ForegroundColor White
Write-Host "  - Parallel workers: 4" -ForegroundColor White
Write-Host ""

if (Test-Path $testImagesDir) {
    dotnet run --project "E:\source\lucidrag\src\LucidRAG.ImageCli\LucidRAG.ImageCli.csproj" -- batch "$testImagesDir" --pattern "**/*.png" --filter-type Screenshot --max-parallel 4 --format table
} else {
    Write-Host "Test images folder not found. Skipping batch demo." -ForegroundColor Red
}

Write-Host ""
Write-Host ""

# Demo 5: Deduplication
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Demo 5: Image Deduplication" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

Write-Host "Finding duplicate images using perceptual hashing:" -ForegroundColor Yellow
Write-Host "  - Algorithm: PDQ (Facebook's robust hash)" -ForegroundColor White
Write-Host "  - Threshold: 5 (Hamming distance)" -ForegroundColor White
Write-Host "  - Action: report (dry run)" -ForegroundColor White
Write-Host ""

if (Test-Path $testImagesDir) {
    dotnet run --project "E:\source\lucidrag\src\LucidRAG.ImageCli\LucidRAG.ImageCli.csproj" -- dedupe "$testImagesDir" --threshold 5 --action report
} else {
    Write-Host "Test images folder not found. Skipping deduplication demo." -ForegroundColor Red
}

Write-Host ""
Write-Host ""

# Summary
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Demo Complete!" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

Write-Host "Key Features Demonstrated:" -ForegroundColor Green
Write-Host "  ✓ Console pixel art preview (4 rendering modes)" -ForegroundColor White
Write-Host "  ✓ Comprehensive forensic analysis" -ForegroundColor White
Write-Host "  ✓ Natural language queries" -ForegroundColor White
Write-Host "  ✓ Batch processing with parallel workers" -ForegroundColor White
Write-Host "  ✓ Perceptual hash deduplication" -ForegroundColor White
Write-Host "  ✓ OCR with coordinate tracking" -ForegroundColor White
Write-Host "  ✓ Vision LLM verification" -ForegroundColor White
Write-Host ""

Write-Host "For more information, see:" -ForegroundColor Yellow
Write-Host "  - README: E:\source\lucidrag\src\LucidRAG.ImageCli\README.md" -ForegroundColor White
Write-Host "  - SOLID Review: E:\source\lucidrag\SOLID_REVIEW_AND_TESTS.md" -ForegroundColor White
Write-Host ""
