# Comprehensive Vision Provider Comparison Test Script
# Tests Ollama, Anthropic, and OpenAI vision models for OCR accuracy, caption quality, and speed

$ErrorActionPreference = "Stop"

# Configuration
$imageCli = "dotnet run --project src/LucidRAG.ImageCli/LucidRAG.ImageCli.csproj --"
$gifPath = "F:\Gifs"
$outputDir = "test-results-all-providers"
$timestamp = Get-Date -Format "yyyy-MM-dd-HHmmss"

# Define all models to test across providers
$testModels = @(
    # Ollama models (local, free)
    @{ Provider = "Ollama"; Model = "minicpm-v:8b"; Tier = "Balanced"; Cost = "Free" },
    @{ Provider = "Ollama"; Model = "llava:7b"; Tier = "Fast"; Cost = "Free" },
    @{ Provider = "Ollama"; Model = "llava:13b"; Tier = "Powerful"; Cost = "Free" },
    @{ Provider = "Ollama"; Model = "bakllava:7b"; Tier = "Fast"; Cost = "Free" },

    # Anthropic models (API, paid)
    @{ Provider = "Anthropic"; Model = "claude-3-5-sonnet-20241022"; Tier = "Frontier"; Cost = "~$0.003/image" },
    @{ Provider = "Anthropic"; Model = "claude-3-opus-20240229"; Tier = "Frontier+"; Cost = "~$0.015/image" },
    @{ Provider = "Anthropic"; Model = "claude-3-haiku-20240307"; Tier = "Fast"; Cost = "~$0.0004/image" },

    # OpenAI models (API, paid)
    @{ Provider = "OpenAI"; Model = "gpt-4o"; Tier = "Frontier"; Cost = "~$0.005/image" },
    @{ Provider = "OpenAI"; Model = "gpt-4o-mini"; Tier = "Fast"; Cost = "~$0.0015/image" },
    @{ Provider = "OpenAI"; Model = "gpt-4-turbo"; Tier = "Frontier"; Cost = "~$0.01/image" }
)

# Test corpus with ground truth
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
        5
    } else {
        0
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
Write-Host "Multi-Provider Vision Model Comparison" -ForegroundColor Cyan
Write-Host "========================================`n" -ForegroundColor Cyan

# Check provider availability
Write-Host "Checking provider availability..." -ForegroundColor Yellow
$providerStatus = @{}

# Check Ollama
try {
    $ollamaCheck = ollama list 2>&1
    if ($LASTEXITCODE -eq 0) {
        $providerStatus["Ollama"] = "Available"
        Write-Host "  ✓ Ollama: Available" -ForegroundColor Green
    } else {
        $providerStatus["Ollama"] = "Not Available"
        Write-Host "  ✗ Ollama: Not Available" -ForegroundColor Red
    }
} catch {
    $providerStatus["Ollama"] = "Not Available"
    Write-Host "  ✗ Ollama: Not Available" -ForegroundColor Red
}

# Check Anthropic (via env var)
if ($env:ANTHROPIC_API_KEY) {
    $providerStatus["Anthropic"] = "Available (API key found)"
    Write-Host "  ✓ Anthropic: Available (API key found)" -ForegroundColor Green
} else {
    $providerStatus["Anthropic"] = "Not Available (no API key)"
    Write-Host "  ⚠ Anthropic: Not Available (set ANTHROPIC_API_KEY)" -ForegroundColor Yellow
}

# Check OpenAI (via env var)
if ($env:OPENAI_API_KEY) {
    $providerStatus["OpenAI"] = "Available (API key found)"
    Write-Host "  ✓ OpenAI: Available (API key found)" -ForegroundColor Green
} else {
    $providerStatus["OpenAI"] = "Not Available (no API key)"
    Write-Host "  ⚠ OpenAI: Not Available (set OPENAI_API_KEY)" -ForegroundColor Yellow
}

Write-Host ""

foreach ($modelSpec in $testModels) {
    $provider = $modelSpec.Provider
    $model = $modelSpec.Model
    $fullModelSpec = "$($provider.ToLower()):$model"

    # Skip if provider not available
    if ($providerStatus[$provider] -notlike "Available*") {
        Write-Host "`n=== Skipping $fullModelSpec (provider not available) ===" -ForegroundColor Gray
        continue
    }

    $tierInfo = "$($modelSpec.Tier) tier, $($modelSpec.Cost)"
    Write-Host "`n=== Testing: $fullModelSpec ($tierInfo) ===" -ForegroundColor Yellow

    $modelResults = @{
        Provider = $provider
        Model = $model
        FullSpec = $fullModelSpec
        Tier = $modelSpec.Tier
        Cost = $modelSpec.Cost
        Tests = @()
        TotalTime = 0
        SuccessCount = 0
        FailureCount = 0
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
            # Run ImageCli with provider:model format
            $output = & dotnet run --project src/LucidRAG.ImageCli/LucidRAG.ImageCli.csproj -- `
                analyze "$gifFullPath" `
                --model "$fullModelSpec" `
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

                # Check OCR success
                $ocrSuccess = $false
                if ($extractedText -and $gif.ExpectedText) {
                    $ocrSuccess = $extractedText -match [regex]::Escape($gif.ExpectedText)
                }

                # Check error correction
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
                $scoreColor = if ($captionQuality.Score -ge 70) { "Green" } elseif ($captionQuality.Score -ge 50) { "Yellow" } else { "Red" }
                Write-Host ("    Caption Score: " + $captionQuality.Score + "/100 (" + $captionQuality.MatchedKeywords + "/" + $captionQuality.TotalKeywords + " kw)") -ForegroundColor $scoreColor
                if ($llmCaption -and $llmCaption.Length -le 100) {
                    Write-Host "    Caption: $llmCaption" -ForegroundColor Gray
                } elseif ($llmCaption) {
                    Write-Host "    Caption: $($llmCaption.Substring(0, 100))..." -ForegroundColor Gray
                }

            } else {
                Write-Host "    ERROR: Failed to parse JSON output" -ForegroundColor Red
                Write-Host "    Raw output: $output" -ForegroundColor DarkGray
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
    $safeFileName = $fullModelSpec -replace '[:\\\/]', '_'
    $modelOutputFile = Join-Path $outputDir "$safeFileName-$timestamp.json"
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

    $avgCaptionScore = if ($result.Tests.Count -gt 0) {
        $captionScores = $result.Tests | Where-Object { $_.CaptionScore -ne $null } | ForEach-Object { $_.CaptionScore }
        if ($captionScores.Count -gt 0) {
            [math]::Round(($captionScores | Measure-Object -Average).Average, 1)
        } else { 0 }
    } else { 0 }

    $comparisonTable += [PSCustomObject]@{
        Provider = $result.Provider
        Model = $result.Model
        Tier = $result.Tier
        Cost = $result.Cost
        AvgTime = "$($result.AverageTime)s"
        OcrAccuracy = "$ocrAccuracy%"
        CaptionScore = "$avgCaptionScore/100"
        Corrections = "$($result.Tests.Where({$_.ErrorCorrected}).Count)/$($result.Tests.Count)"
    }
}

# Sort by provider, then tier
$comparisonTable = $comparisonTable | Sort-Object Provider, @{Expression={
    switch ($_.Tier) {
        "Fast" { 1 }
        "Balanced" { 2 }
        "Powerful" { 3 }
        "Frontier" { 4 }
        "Frontier+" { 5 }
        default { 99 }
    }
}}

$comparisonTable | Format-Table -AutoSize

# Save full comparison
$comparisonFile = Join-Path $outputDir "comparison-all-providers-$timestamp.json"
@{
    Timestamp = $timestamp
    ProviderStatus = $providerStatus
    Models = $allResults
    Summary = $comparisonTable
} | ConvertTo-Json -Depth 10 | Out-File $comparisonFile

Write-Host "`nFull comparison saved to: $comparisonFile" -ForegroundColor Green

# Generate markdown report
$markdownFile = Join-Path $outputDir "ALL-PROVIDERS-RESULTS-$timestamp.md"
$markdown = @"
# Multi-Provider Vision Model Comparison Results

**Date**: $(Get-Date -Format "yyyy-MM-dd HH:mm:ss")
**Test Corpus**: $($testGifs.Count) GIFs with varying OCR difficulty
**Providers**: Ollama (local), Anthropic (API), OpenAI (API)

## Provider Status

"@

foreach ($provider in $providerStatus.Keys | Sort-Object) {
    $status = $providerStatus[$provider]
    if ($status -like "Available*") {
        $icon = "+"
    } else {
        $icon = "-"
    }
    $markdown += "- [$icon] **$provider**: $status`n"
}

$markdown += @"

## Summary Table

| Provider | Model | Tier | Cost | Avg Time | OCR Accuracy | Caption Score | Corrections |
|----------|-------|------|------|----------|--------------|---------------|-------------|
"@

foreach ($row in $comparisonTable) {
    $markdown += "| $($row.Provider) | $($row.Model) | $($row.Tier) | $($row.Cost) | $($row.AvgTime) | $($row.OcrAccuracy) | $($row.CaptionScore) | $($row.Corrections) |`n"
}

$markdown += @"

## Key Findings

### Speed
- **Fastest**: $(($comparisonTable | Sort-Object {[double]($_.AvgTime -replace 's','')} | Select-Object -First 1).Provider):$(($comparisonTable | Sort-Object {[double]($_.AvgTime -replace 's','')} | Select-Object -First 1).Model)
- **Slowest**: $(($comparisonTable | Sort-Object {[double]($_.AvgTime -replace 's','')} -Descending | Select-Object -First 1).Provider):$(($comparisonTable | Sort-Object {[double]($_.AvgTime -replace 's','')} -Descending | Select-Object -First 1).Model)

### OCR Accuracy
- **Best**: $(($comparisonTable | Sort-Object {[double]($_.OcrAccuracy -replace '%','')} -Descending | Select-Object -First 1).Provider):$(($comparisonTable | Sort-Object {[double]($_.OcrAccuracy -replace '%','')} -Descending | Select-Object -First 1).Model)

### Caption Quality
- **Best**: $(($comparisonTable | Sort-Object {[double]($_.CaptionScore -replace '/100','')} -Descending | Select-Object -First 1).Provider):$(($comparisonTable | Sort-Object {[double]($_.CaptionScore -replace '/100','')} -Descending | Select-Object -First 1).Model)

## Recommendations

- **Free/Local**: Use Ollama models (no API costs, runs offline)
- **Best Quality**: Frontier models (Anthropic Claude 3.5 Sonnet or OpenAI GPT-4o)
- **Cost-Effective**: Anthropic Haiku or OpenAI GPT-4o-mini for API usage
- **Speed**: bakllava:7b (Ollama) or Haiku/GPT-4o-mini (API)

"@

$markdown | Out-File $markdownFile -Encoding UTF8

Write-Host "`nMarkdown report saved to: $markdownFile" -ForegroundColor Green
Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host "Testing Complete!" -ForegroundColor Cyan
Write-Host "========================================`n" -ForegroundColor Cyan
