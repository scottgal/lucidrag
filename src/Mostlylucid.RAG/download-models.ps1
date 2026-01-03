# PowerShell script to download ONNX models for semantic search

$ErrorActionPreference = "Stop"

Write-Host "=== Downloading Semantic Search Models ===" -ForegroundColor Cyan
Write-Host ""

# Determine the models directory
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$ModelsDir = Join-Path (Join-Path (Join-Path $ScriptDir "..") "Mostlylucid") "models"

# Create models directory if it doesn't exist
if (!(Test-Path $ModelsDir))
{
    Write-Host "Creating models directory: $ModelsDir" -ForegroundColor Yellow
    New-Item -ItemType Directory -Path $ModelsDir | Out-Null
}

Write-Host "Models will be saved to: $ModelsDir" -ForegroundColor Green
Write-Host ""

# Model URLs from Hugging Face
$ModelUrl = "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/onnx/model.onnx"
$VocabUrl = "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/vocab.txt"
$TokenizerUrl = "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/tokenizer.json"

# Download paths
$ModelPath = Join-Path $ModelsDir "all-MiniLM-L6-v2.onnx"
$VocabPath = Join-Path $ModelsDir "vocab.txt"
$TokenizerPath = Join-Path $ModelsDir "tokenizer.json"

# Function to download file
function Download-File
{
    param($Url, $OutputPath, $Description)

    if (Test-Path $OutputPath)
    {
        Write-Host "✓ $Description already exists, skipping..." -ForegroundColor Gray
        return
    }

    Write-Host "Downloading $Description..." -ForegroundColor Yellow
    Write-Host "  From: $Url" -ForegroundColor Gray
    Write-Host "  To: $OutputPath" -ForegroundColor Gray

    $ProgressPreference = 'SilentlyContinue'
    Invoke-WebRequest -Uri $Url -OutFile $OutputPath -UseBasicParsing

    $FileSize = (Get-Item $OutputPath).Length / 1MB
    Write-Host "✓ Downloaded successfully ($([math]::Round($FileSize, 2)) MB)" -ForegroundColor Green
    Write-Host ""
}

# Download all files
Download-File -Url $ModelUrl -OutputPath $ModelPath -Description "ONNX Model (all-MiniLM-L6-v2)"
Download-File -Url $VocabUrl -OutputPath $VocabPath -Description "Vocabulary File"
Download-File -Url $TokenizerUrl -OutputPath $TokenizerPath -Description "Tokenizer Config"

Write-Host ""
Write-Host "=== Download Complete ===" -ForegroundColor Green
Write-Host ""
Write-Host "Model files are ready at:" -ForegroundColor Cyan
Write-Host "  $ModelsDir" -ForegroundColor White
Write-Host ""
Write-Host "Files downloaded:" -ForegroundColor Cyan
Get-ChildItem $ModelsDir | ForEach-Object {
    $SizeMB = [math]::Round($_.Length / 1MB, 2)
    Write-Host "  - $($_.Name) ($SizeMB MB)" -ForegroundColor White
}
Write-Host ""
Write-Host "You can now run the semantic search demo!" -ForegroundColor Green
