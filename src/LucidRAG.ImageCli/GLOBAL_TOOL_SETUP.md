# .NET Global Tool Setup Guide

This guide explains how to package, test, and publish `lucidrag-image` as a .NET Global Tool.

## Quick Summary

```bash
# Pack the tool locally
dotnet pack src/LucidRAG.ImageCli/LucidRAG.ImageCli.csproj

# Install from local nupkg (testing)
dotnet tool install -g LucidRAG.ImageCli --add-source ./nupkg

# Use the tool
lucidrag-image --version
lucidrag-image analyze photo.jpg

# Uninstall
dotnet tool uninstall -g LucidRAG.ImageCli

# Publish to NuGet.org (when ready)
dotnet nuget push ./nupkg/LucidRAG.ImageCli.1.0.0.nupkg --api-key YOUR_API_KEY --source https://api.nuget.org/v3/index.json
```

## Step-by-Step Setup

### 1. Local Build & Test

Build and pack the tool:

```bash
cd E:\source\lucidrag

# Build the CLI project
dotnet build src/LucidRAG.ImageCli/LucidRAG.ImageCli.csproj

# Pack as NuGet package (outputs to ./nupkg/)
dotnet pack src/LucidRAG.ImageCli/LucidRAG.ImageCli.csproj

# Verify package created
dir nupkg\LucidRAG.ImageCli.1.0.0.nupkg
```

Expected output in `./nupkg/`:
- `LucidRAG.ImageCli.1.0.0.nupkg`
- `LucidRAG.ImageCli.1.0.0.symbols.nupkg` (if configured)

### 2. Local Installation & Testing

Install from local package source:

```bash
# Install from local nupkg folder
dotnet tool install -g LucidRAG.ImageCli --add-source ./nupkg

# Verify installation
dotnet tool list -g | findstr LucidRAG

# Test the tool
lucidrag-image --version
lucidrag-image --help
lucidrag-image analyze src/Mostlylucid.DocSummarizer.Images.Tests/TestImages/icon.png
```

**Troubleshooting local install:**

If you get "Tool already installed", uninstall first:
```bash
dotnet tool uninstall -g LucidRAG.ImageCli
```

If you make changes and want to update:
```bash
# Increment version in .csproj first (1.0.0 -> 1.0.1)
dotnet pack src/LucidRAG.ImageCli/LucidRAG.ImageCli.csproj
dotnet tool update -g LucidRAG.ImageCli --add-source ./nupkg
```

### 3. Publish to NuGet.org

When ready for public release:

#### A. Get NuGet API Key

1. Go to https://www.nuget.org/account/apikeys
2. Click "Create" to generate a new API key
3. Set scope to "Push new packages and package versions"
4. Save the API key securely (can't view again)

#### B. Push Package

```bash
# Push to NuGet.org
dotnet nuget push ./nupkg/LucidRAG.ImageCli.1.0.0.nupkg \
  --api-key YOUR_API_KEY \
  --source https://api.nuget.org/v3/index.json

# Verification (may take 5-10 minutes to appear)
# Visit: https://www.nuget.org/packages/LucidRAG.ImageCli
```

#### C. Public Installation

Once published, users can install globally:

```bash
dotnet tool install -g LucidRAG.ImageCli
lucidrag-image --version
```

### 4. CI/CD Automation with GitHub Actions

Create `.github/workflows/publish-imagecli-tool.yml`:

```yaml
name: Publish ImageCli Tool

on:
  push:
    tags:
      - 'imagecli-v*'

jobs:
  publish:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4

      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'

      - name: Pack tool
        run: dotnet pack src/LucidRAG.ImageCli/LucidRAG.ImageCli.csproj -c Release

      - name: Publish to NuGet
        run: dotnet nuget push ./nupkg/*.nupkg --api-key ${{ secrets.NUGET_API_KEY }} --source https://api.nuget.org/v3/index.json
```

Trigger release:
```bash
git tag imagecli-v1.0.0
git push origin imagecli-v1.0.0
```

## Version Management

Update version in `.csproj` before each release:

```xml
<Version>1.0.1</Version>
<PackageReleaseNotes>
  - Added GIF frame extraction
  - Fixed caching bug
  - Improved performance
</PackageReleaseNotes>
```

**Semantic Versioning:**
- **1.0.0** → **1.0.1**: Bug fixes
- **1.0.0** → **1.1.0**: New features (backward compatible)
- **1.0.0** → **2.0.0**: Breaking changes

## Package Contents

The NuGet package includes:

```
LucidRAG.ImageCli.1.0.0.nupkg
├── lib/net10.0/
│   ├── lucidrag-image.dll
│   ├── Mostlylucid.DocSummarizer.Images.dll
│   └── [dependencies...]
├── tools/net10.0/any/
│   ├── lucidrag-image.exe (Windows)
│   ├── lucidrag-image (Unix)
│   └── DotnetToolSettings.xml
├── README.md
└── [package metadata]
```

## Configuration Files

The tool looks for `appsettings.json` in these locations (in order):

1. Current working directory: `./appsettings.json`
2. User profile: `~/.lucidrag/appsettings.json`
3. Tool installation directory (default config)

**Create user config:**

```bash
# Windows
mkdir %USERPROFILE%\.lucidrag
copy src\LucidRAG.ImageCli\appsettings.json %USERPROFILE%\.lucidrag\

# Linux/Mac
mkdir -p ~/.lucidrag
cp src/LucidRAG.ImageCli/appsettings.json ~/.lucidrag/
```

Edit `~/.lucidrag/appsettings.json`:

```json
{
  "Escalation": {
    "AutoEscalateEnabled": true,
    "ConfidenceThreshold": 0.7
  },
  "VisionLlm": {
    "BaseUrl": "http://localhost:11434",
    "Model": "minicpm-v:8b"
  }
}
```

## Dependencies

The tool packages all dependencies, including:

- `Mostlylucid.DocSummarizer.Images` - Image analysis library
- `System.CommandLine` - CLI framework
- `Spectre.Console` - Terminal UI
- `SixLabors.ImageSharp` - Image processing
- All transitive dependencies

**Native Dependencies:**

SQLite native library is included via `Microsoft.Data.Sqlite.Core` + runtime packages.

## Testing Checklist

Before publishing:

- [ ] `dotnet pack` succeeds without warnings
- [ ] Local install works: `dotnet tool install -g LucidRAG.ImageCli --add-source ./nupkg`
- [ ] Tool runs: `lucidrag-image --version`
- [ ] Basic command works: `lucidrag-image analyze test.jpg`
- [ ] Batch processing works: `lucidrag-image batch ./images --max-parallel 4`
- [ ] Caching works (run same command twice, check cache hits)
- [ ] Escalation works (if Ollama running)
- [ ] JSON-LD export works: `lucidrag-image batch ./images --export-jsonld output.jsonld`
- [ ] README.md displays correctly on nuget.org preview
- [ ] Version number updated in .csproj
- [ ] Release notes updated

## Troubleshooting

### "Tool 'lucidrag-image' is already installed"

```bash
dotnet tool uninstall -g LucidRAG.ImageCli
dotnet tool install -g LucidRAG.ImageCli --add-source ./nupkg
```

### "Could not find a part of the path"

Check `appsettings.json` is marked as `CopyToOutputDirectory`:

```xml
<None Update="appsettings.json">
  <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
</None>
```

### "The specified framework 'Microsoft.NETCore.App', version '10.0.0' was not found"

User needs .NET 10 SDK installed:

```bash
# Check installed SDKs
dotnet --list-sdks

# Install .NET 10 SDK
# Download from: https://dotnet.microsoft.com/download/dotnet/10.0
```

### Package not appearing on NuGet.org

- Wait 5-10 minutes for indexing
- Check https://www.nuget.org/packages/LucidRAG.ImageCli/manage
- Verify push succeeded (check API response)
- Ensure package validation passed

### "NU5100: The assembly is not inside lib folder"

This is normal for tools - they go in `tools/` folder, not `lib/`.

## Advanced: Multi-Targeting

To support older .NET versions:

```xml
<TargetFrameworks>net10.0;net8.0</TargetFrameworks>
```

**Consideration:** .NET 10 features (like required members) won't work on net8.0.

## Security

**Best Practices:**

1. **Never commit API keys** - Use GitHub Secrets for CI/CD
2. **Sign packages** (optional):
   ```xml
   <SignAssembly>true</SignAssembly>
   <AssemblyOriginatorKeyFile>key.snk</AssemblyOriginatorKeyFile>
   ```
3. **Enable NuGet package signing** (recommended for public tools)

## Distribution Alternatives

### Local Network Share

```bash
# Share package on network
\\fileserver\nuget\LucidRAG.ImageCli.1.0.0.nupkg

# Install from network share
dotnet tool install -g LucidRAG.ImageCli --add-source \\fileserver\nuget
```

### GitHub Packages

```bash
# Configure GitHub Packages source
dotnet nuget add source https://nuget.pkg.github.com/scottgal/index.json \
  -n github -u scottgal -p YOUR_GITHUB_PAT

# Push to GitHub Packages
dotnet nuget push ./nupkg/*.nupkg --source github
```

### Azure Artifacts

```bash
# Add Azure Artifacts feed
dotnet nuget add source https://pkgs.dev.azure.com/yourorg/_packaging/yourfeed/nuget/v3/index.json

# Push to Azure Artifacts
dotnet nuget push ./nupkg/*.nupkg --source https://pkgs.dev.azure.com/yourorg/_packaging/yourfeed/nuget/v3/index.json
```

## Post-Publication

After publishing to NuGet.org:

1. **Update README** - Add NuGet badge:
   ```markdown
   [![NuGet](https://img.shields.io/nuget/v/LucidRAG.ImageCli.svg)](https://www.nuget.org/packages/LucidRAG.ImageCli/)
   ```

2. **Create Release** on GitHub with:
   - Tag: `imagecli-v1.0.0`
   - Title: `ImageCli v1.0.0`
   - Description: Copy from PackageReleaseNotes
   - Attach: `LucidRAG.ImageCli.1.0.0.nupkg`

3. **Announce** on:
   - GitHub Discussions
   - Project README
   - Twitter/social media

## Monitoring

Track downloads and usage:

- NuGet stats: https://www.nuget.org/stats/packages/LucidRAG.ImageCli
- Download count visible on package page
- User feedback via GitHub Issues

## License

This tool is published under the MIT License (specified in `PackageLicenseExpression`).
