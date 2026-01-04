# Vision LLM Model Comparison Test Script
# Tests 4 vision models on 5 GIFs with varying OCR difficulty

$ErrorActionPreference = "Stop"

# Configuration
$imageCli = "dotnet run --project src/LucidRAG.ImageCli/LucidRAG.ImageCli.csproj --"
$gifPath = "F:\Gifs"
$outputDir = "test-results"
$timestamp = Get-Date -Format "yyyy-MM-dd-HHmmss"

# Models to test
$models = @(
    'minicpm-v:8b',
    'llava:7b',
    'llava:13b',
    'bakllava:7b'
)

# Test corpus - GIFs with varying OCR difficulty + caption ground truth
$testGifs = @(
    @{
        Name = 'BackOfTheNet.gif'
        ExpectedText = 'Back of the net'
        KnownError = 'Back Bf the net'
        ExpectedCaption = 'Alan Partridge celebrating with text saying Back of the net'
        CaptionKeywords = @('Alan', 'Partridge', 'celebrating', 'back', 'net')
    },
    @{
        Name = 'anchorman-not-even-mad.gif'
        ExpectedText = "I'm not even mad"
        KnownError = $null
        ExpectedCaption = 'Will Ferrell as Ron Burgundy with text I am not even mad that is amazing'
        CaptionKeywords = @('Will Ferrell', 'Burgundy', 'not even mad', 'amazing')
    },
    @{
        Name = 'aed.gif'
        ExpectedText = 'You keep using that word'
        KnownError = $null
        ExpectedCaption = 'Inigo Montoya from Princess Bride saying You keep using that word'
        CaptionKeywords = @('Inigo', 'Montoya', 'Princess Bride', 'keep using')
    },
    @{
        Name = 'arse_biscuits.gif'
        ExpectedText = 'ARSE BISCUITS'
        KnownError = $null
        ExpectedCaption = 'Text saying ARSE BISCUITS in bold letters'
        CaptionKeywords = @('ARSE', 'BISCUITS', 'text', 'bold')
    },
    @{
        Name = 'animatedbullshit.gif'
        ExpectedText = 'ANIMATED BULLSHIT'
        KnownError = 'ph ima "|"'
        ExpectedCaption = 'Animated text displaying ANIMATED BULLSHIT with effects'
        CaptionKeywords = @('animated', 'bullshit', 'text')
    }
)

# Create output directory
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null

# Function to score caption quality
function Get-CaptionQuality {
    param(
        [string]$Caption,
        [array]$Keywords,
        [string]$ExpectedCaption
    )

    if (-not $Caption) {
        return @{
            Score = 0
            MatchedKeywords = 0
            TotalKeywords = $Keywords.Count
            Length = 0
            Percentage = 0
        }
    }

    # Count keyword matches (case-insensitive)
    $matchedCount = 0
    foreach ($keyword in $Keywords) {
        if ($Caption -match [regex]::Escape($keyword)) {
            $matchedCount++
        }
    }

    # Calculate score
    $keywordScore = if ($Keywords.Count -gt 0) {
        ($matchedCount / $Keywords.Count) * 100
    } else { 0 }

    # Bonus for length (captions should be descriptive, 50-200 chars is good)
    $lengthBonus = if ($Caption.Length -ge 50 -and $Caption.Length -le 200) {
        10
    } elseif ($Caption.Length -gt 200) {
        5  # Very long captions get small bonus
    } else {
        0  # Too short
    }

    $totalScore = [math]::Min(100, $keywordScore + $lengthBonus)

    return @{
        Score = [math]::Round($totalScore, 1)
        MatchedKeywords = $matchedCount
        TotalKeywords = $Keywords.Count
        Length = $Caption.Length
        Percentage = [math]::Round($keywordScore, 1)
    }
}

# Initialize results
$allResults = @()

Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host "Vision LLM Model Comparison Test" -ForegroundColor Cyan
Write-Host "========================================`n" -ForegroundColor Cyan

foreach ($model in $models) {
    Write-Host "`n=== Testing model: $model ===" -ForegroundColor Yellow

    $modelResults = @{
        Model = $model
        Tests = @()
        TotalTime = 0
        SuccessCount = 0
        FailureCount = 0
    }

    # Check if model is available
    Write-Host "Checking if $model is available..." -ForegroundColor Gray
    $modelList = ollama list | Select-String $model.Split(':')[0]
    if (-not $modelList) {
        Write-Host "WARNING: Model $model not found. Skipping..." -ForegroundColor Red
        Write-Host "Install with: ollama pull $model" -ForegroundColor Yellow
        continue
    }

    foreach ($gif in $testGifs) {
        $gifFullPath = Join-Path $gifPath $gif.Name

        if (-not (Test-Path $gifFullPath)) {
            Write-Host "  WARNING: GIF not found: $gifFullPath" -ForegroundColor Red
            continue
        }

        Write-Host "`n  Testing: $($gif.Name)..." -ForegroundColor White

        $startTime = Get-Date

        try {
            # Run ImageCli with --model and --use-llm flags
            $output = & dotnet run --project src/LucidRAG.ImageCli/LucidRAG.ImageCli.csproj -- `
                analyze "$gifFullPath" `
                --model $model `
                --use-llm `
                --include-ocr `
                --format json `
                2>&1

            $endTime = Get-Date
            $duration = ($endTime - $startTime).TotalSeconds

            # Parse JSON output
            $jsonOutput = $output | Out-String | ConvertFrom-Json -ErrorAction SilentlyContinue

            if ($jsonOutput) {
                $extractedText = $jsonOutput.extracted_text
                $llmCaption = $jsonOutput.llm_caption

                # Check if OCR was successful
                $ocrSuccess = $false
                if ($extractedText -and $gif.ExpectedText) {
                    $ocrSuccess = $extractedText -match [regex]::Escape($gif.ExpectedText)
                }

                # Check if known error was corrected
                $errorCorrected = $false
                if ($gif.KnownError -and $extractedText) {
                    $errorCorrected = $extractedText -notmatch [regex]::Escape($gif.KnownError)
                }

                # Evaluate caption quality
                $captionQuality = Get-CaptionQuality -Caption $llmCaption `
                    -Keywords $gif.CaptionKeywords `
                    -ExpectedCaption $gif.ExpectedCaption

                $testResult = @{
                    GifName = $gif.Name
                    Duration = [math]::Round($duration, 2)
                    ExtractedText = $extractedText
                    LlmCaption = $llmCaption
                    OcrSuccess = $ocrSuccess
                    ErrorCorrected = $errorCorrected
                    CaptionScore = $captionQuality.Score
                    CaptionMatches = "$($captionQuality.MatchedKeywords)/$($captionQuality.TotalKeywords)"
                    CaptionLength = $captionQuality.Length
                    Success = $true
                }

                $modelResults.Tests += $testResult
                $modelResults.TotalTime += $duration
                $modelResults.SuccessCount++

                Write-Host "    Time: $($duration)s" -ForegroundColor Green
                Write-Host "    Extracted: $extractedText" -ForegroundColor Cyan
                if ($ocrSuccess) {
                    Write-Host "    OCR: CORRECT" -ForegroundColor Green
                } else {
                    Write-Host "    OCR: INCORRECT/PARTIAL" -ForegroundColor Yellow
                }
                Write-Host "    Caption Score: $($captionQuality.Score)/100 ($($captionQuality.MatchedKeywords)/$($captionQuality.TotalKeywords) keywords)" -ForegroundColor $(if ($captionQuality.Score -ge 70) { "Green" } elseif ($captionQuality.Score -ge 50) { "Yellow" } else { "Red" })
                if ($llmCaption.Length -le 100) {
                    Write-Host "    Caption: $llmCaption" -ForegroundColor Gray
                } else {
                    Write-Host "    Caption: $($llmCaption.Substring(0, 100))..." -ForegroundColor Gray
                }
            } else {
                Write-Host "    ERROR: Failed to parse JSON output" -ForegroundColor Red
                $modelResults.FailureCount++
            }

        } catch {
            $endTime = Get-Date
            $duration = ($endTime - $startTime).TotalSeconds

            Write-Host "    ERROR: $($_.Exception.Message)" -ForegroundColor Red

            $testResult = @{
                GifName = $gif.Name
                Duration = [math]::Round($duration, 2)
                Error = $_.Exception.Message
                Success = $false
            }

            $modelResults.Tests += $testResult
            $modelResults.FailureCount++
        }
    }

    # Calculate average time
    if ($modelResults.Tests.Count -gt 0) {
        $modelResults.AverageTime = [math]::Round($modelResults.TotalTime / $modelResults.Tests.Count, 2)
    }

    $allResults += $modelResults

    # Save individual model results
    $modelOutputFile = Join-Path $outputDir "$model-$timestamp.json"
    $modelResults | ConvertTo-Json -Depth 10 | Out-File $modelOutputFile
    Write-Host "`n  Results saved to: $modelOutputFile" -ForegroundColor Green
}

# Generate comparison report
Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host "Comparison Summary" -ForegroundColor Cyan
Write-Host "========================================`n" -ForegroundColor Cyan

$comparisonTable = @()
foreach ($result in $allResults) {
    $successRate = if ($result.Tests.Count -gt 0) {
        [math]::Round(($result.SuccessCount / $result.Tests.Count) * 100, 1)
    } else { 0 }

    $ocrCorrectCount = ($result.Tests | Where-Object { $_.OcrSuccess -eq $true }).Count
    $ocrAccuracy = if ($result.Tests.Count -gt 0) {
        [math]::Round(($ocrCorrectCount / $result.Tests.Count) * 100, 1)
    } else { 0 }

    # Calculate average caption score
    $avgCaptionScore = if ($result.Tests.Count -gt 0) {
        $captionScores = $result.Tests | Where-Object { $_.CaptionScore -ne $null } | ForEach-Object { $_.CaptionScore }
        if ($captionScores.Count -gt 0) {
            [math]::Round(($captionScores | Measure-Object -Average).Average, 1)
        } else { 0 }
    } else { 0 }

    $comparisonTable += [PSCustomObject]@{
        Model = $result.Model
        AvgTime = "$($result.AverageTime)s"
        SuccessRate = "$successRate%"
        OcrAccuracy = "$ocrAccuracy%"
        CaptionScore = "$avgCaptionScore/100"
        Corrections = "$($result.Tests.Where({$_.ErrorCorrected}).Count)/$($result.Tests.Count)"
    }
}

$comparisonTable | Format-Table -AutoSize

# Save comparison report
$comparisonFile = Join-Path $outputDir "comparison-$timestamp.json"
@{
    Timestamp = $timestamp
    Models = $allResults
    Summary = $comparisonTable
} | ConvertTo-Json -Depth 10 | Out-File $comparisonFile

Write-Host "`nFull comparison saved to: $comparisonFile" -ForegroundColor Green

# Generate markdown report
$markdownFile = Join-Path $outputDir "VISION-MODEL-RESULTS-$timestamp.md"
$markdown = @"
# Vision LLM Model Comparison Results

**Date**: $(Get-Date -Format "yyyy-MM-dd HH:mm:ss")
**Test Corpus**: $($testGifs.Count) GIFs with varying OCR difficulty

## Summary Table

| Model | Avg Time (s) | Success Rate | OCR Accuracy | Caption Score | Corrections |
|-------|--------------|--------------|--------------|---------------|-------------|
"@

foreach ($row in $comparisonTable) {
    $markdown += "| $($row.Model) | $($row.AvgTime) | $($row.SuccessRate) | $($row.OcrAccuracy) | $($row.CaptionScore) | $($row.Corrections) |`n"
}

$markdown += @"

## Detailed Results

"@

foreach ($result in $allResults) {
    $markdown += @"

### Model: $($result.Model)

**Average Time**: $($result.AverageTime)s
**Success Rate**: $($result.SuccessCount)/$($result.Tests.Count)

#### Test Results

| GIF | Time (s) | Extracted Text | OCR Status | Caption Score | Caption |
|-----|----------|----------------|------------|---------------|---------|
"@

    foreach ($test in $result.Tests) {
        $status = if ($test.OcrSuccess) { "✅ Correct" } elseif ($test.Success) { "⚠️ Partial" } else { "❌ Failed" }
        $captionPreview = if ($test.LlmCaption -and $test.LlmCaption.Length -gt 60) {
            $test.LlmCaption.Substring(0, 60) + "..."
        } else {
            $test.LlmCaption
        }
        $markdown += "| $($test.GifName) | $($test.Duration) | $($test.ExtractedText) | $status | $($test.CaptionScore) | $captionPreview |`n"
    }
}

$markdown += @"

## Recommendations

Based on these results:

- **General Use**: Choose the model with best balance of speed and accuracy
- **Speed-Critical**: Choose the fastest model with acceptable accuracy (>75%)
- **Quality-Critical**: Choose the most accurate model regardless of speed

"@

$markdown | Out-File $markdownFile -Encoding UTF8

Write-Host "`nMarkdown report saved to: $markdownFile" -ForegroundColor Green
Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host "Testing Complete!" -ForegroundColor Cyan
Write-Host "========================================`n" -ForegroundColor Cyan
