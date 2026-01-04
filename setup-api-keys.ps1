# Setup API Keys for Vision Model Testing
# This script helps you securely configure API keys for OpenAI and Anthropic

Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host "Vision Model API Key Configuration" -ForegroundColor Cyan
Write-Host "========================================`n" -ForegroundColor Cyan

Write-Host "SECURITY NOTE:" -ForegroundColor Yellow
Write-Host "API keys will be stored as USER environment variables." -ForegroundColor Yellow
Write-Host "They will persist across sessions but are scoped to your user account." -ForegroundColor Yellow
Write-Host "`nNever commit API keys to git or share them publicly!`n" -ForegroundColor Red

# Check for existing keys
$existingOpenAI = [System.Environment]::GetEnvironmentVariable('OPENAI_API_KEY', 'User')
$existingAnthropic = [System.Environment]::GetEnvironmentVariable('ANTHROPIC_API_KEY', 'User')

if ($existingOpenAI) {
    Write-Host "✓ OpenAI API key already configured" -ForegroundColor Green
    Write-Host "  Current value: $($existingOpenAI.Substring(0, 10))..." -ForegroundColor Gray
} else {
    Write-Host "⚠ OpenAI API key not configured" -ForegroundColor Yellow
}

if ($existingAnthropic) {
    Write-Host "✓ Anthropic API key already configured" -ForegroundColor Green
    Write-Host "  Current value: $($existingAnthropic.Substring(0, 10))..." -ForegroundColor Gray
} else {
    Write-Host "⚠ Anthropic API key not configured" -ForegroundColor Yellow
}

Write-Host ""

# Prompt for keys
$updateOpenAI = if ($existingOpenAI) {
    Read-Host "Update OpenAI key? (y/N)"
} else {
    "y"
}

if ($updateOpenAI -eq "y" -or $updateOpenAI -eq "Y") {
    $openAIKey = Read-Host "Enter OpenAI API key (starts with sk-proj- or sk-)"
    if ($openAIKey) {
        [System.Environment]::SetEnvironmentVariable('OPENAI_API_KEY', $openAIKey, 'User')
        Write-Host "✓ OpenAI API key configured" -ForegroundColor Green
    }
}

$updateAnthropic = if ($existingAnthropic) {
    Read-Host "Update Anthropic key? (y/N)"
} else {
    "y"
}

if ($updateAnthropic -eq "y" -or $updateAnthropic -eq "Y") {
    $anthropicKey = Read-Host "Enter Anthropic API key (starts with sk-ant-)"
    if ($anthropicKey) {
        [System.Environment]::SetEnvironmentVariable('ANTHROPIC_API_KEY', $anthropicKey, 'User')
        Write-Host "✓ Anthropic API key configured" -ForegroundColor Green
    }
}

Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host "Configuration Complete!" -ForegroundColor Cyan
Write-Host "========================================`n" -ForegroundColor Cyan

Write-Host "To use the keys in your current PowerShell session, run:" -ForegroundColor Yellow
Write-Host '  $env:OPENAI_API_KEY = [System.Environment]::GetEnvironmentVariable("OPENAI_API_KEY", "User")' -ForegroundColor Cyan
Write-Host '  $env:ANTHROPIC_API_KEY = [System.Environment]::GetEnvironmentVariable("ANTHROPIC_API_KEY", "User")' -ForegroundColor Cyan

Write-Host "`nOr simply restart your PowerShell session.`n" -ForegroundColor Yellow

Write-Host "To test the configuration, run:" -ForegroundColor Yellow
Write-Host "  .\test-all-vision-providers.ps1" -ForegroundColor Cyan
Write-Host ""
