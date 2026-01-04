# Vision Model Testing Setup - Complete

**Date**: 2026-01-04
**Status**: ✅ Ready to Test

## What's Been Implemented

### 1. Multi-Provider Vision Support

ImageCli now supports **3 vision providers**:

- **Ollama** (local, free) - minicpm-v, llava, bakllava, etc.
- **Anthropic** (API) - Claude 3.5 Sonnet, Claude 3 Opus, Claude 3 Haiku
- **OpenAI** (API) - GPT-4o, GPT-4o-mini, GPT-4 Turbo

### 2. API Keys Configured

✅ API keys are securely stored in .NET User Secrets:
- OpenAI API key configured
- Anthropic API key configured

**Security**: Keys are stored in `%APPDATA%\Microsoft\UserSecrets\<guid>\secrets.json` and never committed to git.

### 3. Model Switching

You can now specify models in two ways:

```bash
# Format: model-name (uses default provider)
lucidrag-image analyze image.gif --model minicpm-v:8b --use-llm

# Format: provider:model
lucidrag-image analyze image.gif --model anthropic:claude-3-5-sonnet-20241022 --use-llm
lucidrag-image analyze image.gif --model openai:gpt-4o --use-llm
```

### 4. Testing Scripts

Two comprehensive testing scripts created:

#### `test-vision-models.ps1`
- Tests 4 Ollama models on 5 GIFs
- Measures OCR accuracy, caption quality, and speed
- Generates JSON and Markdown reports

#### `test-all-vision-providers.ps1`
- Tests **all providers** (Ollama, Anthropic, OpenAI)
- Tests 10 models total across 3 providers
- Compares speed, cost, OCR accuracy, and caption quality
- Generates comprehensive comparison reports

### 5. Caption Quality Metrics

Both scripts now evaluate:
- **OCR Accuracy**: Does it extract the correct text?
- **Caption Quality Score**: Keyword matching against ground truth
- **Speed**: Processing time per image
- **Error Correction**: Did it fix known OCR errors?

## How to Run Tests

### Quick Test (Single Model)

```powershell
# Test with Ollama (local)
dotnet run --project src/LucidRAG.ImageCli/LucidRAG.ImageCli.csproj -- `
    analyze "F:\Gifs\BackOfTheNet.gif" `
    --model minicpm-v:8b `
    --use-llm --include-ocr --format json

# Test with Anthropic (API)
dotnet run --project src/LucidRAG.ImageCli/LucidRAG.ImageCli.csproj -- `
    analyze "F:\Gifs\BackOfTheNet.gif" `
    --model anthropic:claude-3-5-sonnet-20241022 `
    --use-llm --include-ocr --format json

# Test with OpenAI (API)
dotnet run --project src/LucidRAG.ImageCli/LucidRAG.ImageCli.csproj -- `
    analyze "F:\Gifs\BackOfTheNet.gif" `
    --model openai:gpt-4o `
    --use-llm --include-ocr --format json
```

### Full Ollama Comparison

```powershell
.\test-vision-models.ps1
```

**Tests**: 4 Ollama models × 5 GIFs = 20 test cases
**Output**: `test-results/VISION-MODEL-RESULTS-<timestamp>.md`

### Full Multi-Provider Comparison

```powershell
.\test-all-vision-providers.ps1
```

**Tests**: Up to 10 models × 5 GIFs = 50 test cases
**Output**: `test-results-all-providers/ALL-PROVIDERS-RESULTS-<timestamp>.md`

## Expected Results

Based on the test plan, we expect:

| Provider | Model | Tier | Speed | OCR Accuracy | Caption Quality | Cost |
|----------|-------|------|-------|--------------|-----------------|------|
| Ollama | minicpm-v:8b | Balanced | ~2-3s | 100% (baseline) | High | Free |
| Ollama | llava:7b | Fast | ~1-2s | 85% | Medium | Free |
| Ollama | llava:13b | Powerful | ~3-5s | 110% | Very High | Free |
| Ollama | bakllava:7b | Fast | ~1-2s | 80% | Medium | Free |
| Anthropic | claude-3-5-sonnet | Frontier | ~1-2s | 120%+ | Excellent | $0.003/img |
| Anthropic | claude-3-opus | Frontier+ | ~2-3s | 125%+ | Best | $0.015/img |
| Anthropic | claude-3-haiku | Fast | ~0.5-1s | 100% | Good | $0.0004/img |
| OpenAI | gpt-4o | Frontier | ~1-2s | 115%+ | Excellent | $0.005/img |
| OpenAI | gpt-4o-mini | Fast | ~1s | 95% | Good | $0.0015/img |
| OpenAI | gpt-4-turbo | Frontier | ~2-3s | 110% | Very Good | $0.01/img |

## Test Corpus

5 GIFs with varying difficulty:

1. **BackOfTheNet.gif** - Known OCR error ("Back Bf the net" → "Back of the net")
2. **anchorman-not-even-mad.gif** - Clean text
3. **aed.gif** - Multi-word quote from Princess Bride
4. **arse_biscuits.gif** - Uppercase bold text
5. **animatedbullshit.gif** - Garbled OCR ("ph ima "|"" → "ANIMATED BULLSHIT")

## Next Steps

1. **Run Ollama tests**: `.\test-vision-models.ps1` (local, free)
2. **Review results**: Check OCR accuracy and caption quality
3. **Run frontier comparison**: `.\test-all-vision-providers.ps1` (includes API calls)
4. **Analyze trade-offs**: Speed vs Quality vs Cost
5. **Implement tiered escalation**: Based on test results, implement Fast → Balanced → Powerful escalation path

## Managing User Secrets

```powershell
# View configured secrets
cd src/LucidRAG.ImageCli
dotnet user-secrets list

# Update a key
dotnet user-secrets set "OpenAI:ApiKey" "your-new-key"
dotnet user-secrets set "Anthropic:ApiKey" "your-new-key"

# Remove a key
dotnet user-secrets remove "OpenAI:ApiKey"

# Clear all secrets
dotnet user-secrets clear
```

## Architecture Files

- `VISION-MODEL-COMPARISON-PLAN.md` - Original test plan
- `VISION-ESCALATION-DESIGN.md` - Multi-tier escalation architecture
- `src/LucidRAG.ImageCli/Services/VisionClients/` - Provider implementations
  - `IVisionClient.cs` - Unified interface
  - `OllamaVisionClient.cs` - Local Ollama support
  - `AnthropicVisionClient.cs` - Anthropic Claude Vision API
  - `OpenAIVisionClient.cs` - OpenAI GPT-4 Vision API
- `src/LucidRAG.ImageCli/Services/UnifiedVisionService.cs` - Provider manager

## Security Notes

- ✅ API keys stored in .NET User Secrets (not in git)
- ✅ `appsettings.local.json` added to `.gitignore`
- ⚠️ **IMPORTANT**: Rotate the API keys after testing (they were shared in conversation)
  - OpenAI: https://platform.openai.com/api-keys
  - Anthropic: https://console.anthropic.com/settings/keys

---

**Ready to test!** Run `.\test-all-vision-providers.ps1` to compare all providers.
