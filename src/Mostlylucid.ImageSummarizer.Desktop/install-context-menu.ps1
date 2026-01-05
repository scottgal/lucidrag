# Install-ContextMenu.ps1
# Adds "Get Alt Text" context menu entry for images
# Run as Administrator

param(
    [string]$ExePath = "",
    [switch]$Uninstall
)

$ErrorActionPreference = "Stop"

# Find the executable
if ([string]::IsNullOrEmpty($ExePath)) {
    # Try to find in common locations
    $possiblePaths = @(
        "$PSScriptRoot\bin\Release\net10.0\ImageSummarizer.exe",
        "$PSScriptRoot\bin\Debug\net10.0\ImageSummarizer.exe",
        "$env:LOCALAPPDATA\Programs\ImageSummarizer\ImageSummarizer.exe",
        "$env:ProgramFiles\ImageSummarizer\ImageSummarizer.exe"
    )

    foreach ($path in $possiblePaths) {
        if (Test-Path $path) {
            $ExePath = $path
            break
        }
    }
}

if ([string]::IsNullOrEmpty($ExePath) -or -not (Test-Path $ExePath)) {
    Write-Host "ImageSummarizer.exe not found. Please specify path with -ExePath" -ForegroundColor Red
    Write-Host "Usage: .\install-context-menu.ps1 -ExePath 'C:\path\to\ImageSummarizer.exe'"
    exit 1
}

$ExePath = (Resolve-Path $ExePath).Path
Write-Host "Using: $ExePath" -ForegroundColor Cyan

# Registry paths for image file types
$imageExtensions = @("jpg", "jpeg", "png", "gif", "webp", "bmp")
$registryPaths = @(
    "HKCU:\Software\Classes\SystemFileAssociations\image\shell\ImageSummarizer"
)

# Also add to specific extensions for better compatibility
foreach ($ext in $imageExtensions) {
    $registryPaths += "HKCU:\Software\Classes\.$ext\shell\ImageSummarizer"
}

if ($Uninstall) {
    Write-Host "Removing context menu entries..." -ForegroundColor Yellow
    foreach ($regPath in $registryPaths) {
        if (Test-Path $regPath) {
            Remove-Item -Path $regPath -Recurse -Force
            Write-Host "  Removed: $regPath" -ForegroundColor Gray
        }
    }
    Write-Host "Context menu entries removed!" -ForegroundColor Green
    exit 0
}

Write-Host "Installing context menu entries..." -ForegroundColor Yellow

foreach ($regPath in $registryPaths) {
    # Create the shell key
    if (-not (Test-Path $regPath)) {
        New-Item -Path $regPath -Force | Out-Null
    }

    # Set display name and icon
    Set-ItemProperty -Path $regPath -Name "(Default)" -Value "Get Alt Text"
    Set-ItemProperty -Path $regPath -Name "Icon" -Value "$ExePath,0"

    # Create command subkey
    $commandPath = "$regPath\command"
    if (-not (Test-Path $commandPath)) {
        New-Item -Path $commandPath -Force | Out-Null
    }

    # Set the command
    Set-ItemProperty -Path $commandPath -Name "(Default)" -Value "`"$ExePath`" `"%1`""

    Write-Host "  Added: $regPath" -ForegroundColor Gray
}

Write-Host ""
Write-Host "Context menu installed!" -ForegroundColor Green
Write-Host "Right-click any image file and select 'Get Alt Text'" -ForegroundColor Cyan
Write-Host ""
Write-Host "To uninstall, run: .\install-context-menu.ps1 -Uninstall" -ForegroundColor Gray
