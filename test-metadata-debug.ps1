# Debug script to check if metadata is being returned from Anthropic API
param(
    [string]$TestImage = "E:\source\lucidrag\test-images\chat-ui.png"
)

$ErrorActionPreference = "Stop"

Write-Host "Testing metadata capture..." -ForegroundColor Cyan
Write-Host "Image: $TestImage`n" -ForegroundColor Gray

# Set logging to Debug to see metadata parsing
$env:DOTNET_CLI_CONTEXT_VERBOSE = "true"

# Run with debug logging
dotnet run --project "E:\source\lucidrag\src\LucidRAG.ImageCli" -- `
    score $TestImage `
    --model "anthropic:claude-3-opus-20240229" `
    --goal caption `
    2>&1 | Select-String -Pattern "metadata|Parsed|Tone|Sentiment|Llm|vision_response" -Context 2,2

Write-Host "`nDone" -ForegroundColor Green
