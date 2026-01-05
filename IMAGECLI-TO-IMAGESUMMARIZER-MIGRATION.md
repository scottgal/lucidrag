# ImageCli → ImageSummarizer Migration Plan

## Renaming Requirements

The user wants ImageCli renamed to follow the "Summarizer" naming pattern (like DocSummarizer).

### Project Rename
```
src/ImageCli/ImageCli.csproj → src/Mostlylucid.ImageSummarizer.Cli/Mostlylucid.ImageSummarizer.Cli.csproj
```

### Assembly Names
- **Package ID**: `Mostlylucid.ImageSummarizer.Cli`
- **Tool Command**: `imagesummarizer` (or `image-summary`)
- **Namespace**: `Mostlylucid.ImageSummarizer.Cli`

### GitHub Action Updates
- Release workflow tag: `imagesummarizer-v*.*.*`
- Artifact names: `imagesummarizer-{platform}.{ext}`

## MCP Mode Implementation

Add `--mcp` flag that runs tool as MCP server.

### MCP Tool: `extract_text_from_image`

```csharp
[McpServerTool(Name = "extract_text_from_image")]
[Description("Extract text from images (GIF, PNG, JPG) using advanced OCR. Supports animations with temporal voting.")]
public static async Task<string> ExtractTextFromImageAsync(
    [Description("Path to image file")] string imagePath,
    [Description("OCR pipeline: simple, advanced, or quality")] string pipeline = "advanced",
    [Description("Include quality signals in response")] bool includeSignals = false)
{
    // Implementation
}
```

### MCP Tool: `analyze_image_quality`

```csharp
[McpServerTool(Name = "analyze_image_quality")]
[Description("Analyze image quality metrics: text likeliness, sharpness, color analysis, motion (for GIFs).")]
public static async Task<string> AnalyzeImageQualityAsync(
    [Description("Path to image file")] string imagePath)
{
    // Implementation
}
```

### MCP Tool: `list_ocr_pipelines`

```csharp
[McpServerTool(Name = "list_ocr_pipelines")]
[Description("List available OCR pipelines with details on speed, quality, and features.")]
public static async Task<string> ListOcrPipelinesAsync()
{
    // Implementation
}
```

## Implementation Approach

Given time constraints, recommend **hybrid approach**:

###Option 1: Add `--mcp` flag to existing tool (RECOMMENDED)
```csharp
var mcpOpt = new Option<bool>("--mcp", "Run as MCP server");
rootCommand.AddOption(mcpOpt);

if (mcp)
{
    await RunMcpServer();
}
else
{
    await ProcessImage(...);
}
```

### Option 2: Separate MCP project (like DocSummarizer.Mcp)
Create `Mostlylucid.ImageSummarizer.Mcp` project.

**Pros**: Clean separation
**Cons**: More files, longer setup

## MCP Tool Discoverability

MCP clients (Claude Desktop) automatically discover tools via stdio protocol.

**Required**:
1. `AddMcpServer()` - Registers MCP server
2. `WithStdioServerTransport()` - Uses stdio for communication
3. `WithToolsFromAssembly()` - Auto-discovers `[McpServerTool]` attributes
4. Tool descriptions via `[Description]` attributes

**Claude Desktop config**:
```json
{
  "mcpServers": {
    "image-ocr": {
      "command": "imagesummarizer",
      "args": ["--mcp"]
    }
  }
}
```

## Package Dependencies

Add to project:
```xml
<PackageReference Include="ModelContextProtocol.Server" Version="1.0.0" />
```

## Testing MCP Mode

```bash
# Start MCP server
imagesummarizer --mcp

# Test via stdio (send JSON-RPC 2.0 messages)
echo '{"jsonrpc":"2.0","method":"tools/list","id":1}' | imagesummarizer --mcp
```

## Files to Update

### Code Files
- [ ] `src/ImageCli/ImageCli.csproj` → Rename project
- [ ] `src/ImageCli/Program.cs` → Add MCP mode
- [ ] `src/ImageCli/Tools/ImageOcrTools.cs` → NEW: MCP tool definitions
- [ ] `src/ImageCli/README.md` → Update all references
- [ ] `src/ImageCli/COMMAND-REFERENCE.md` → Add `--mcp` documentation

### CI/CD Files
- [ ] `.github/workflows/release-imagecli.yml` → Update names and tags
- [ ] `LucidRAG.sln` → Update project references

### Documentation Files
- [ ] `README.md` → Update project structure
- [ ] `IMAGECLI-PHILOSOPHY.md` → Rename references
- [ ] `CODE-QUALITY-IMPROVEMENTS.md` → Update references

## Implementation Priority

1. **Add MCP mode to existing ImageCli** (1-2 hours)
2. **Test MCP integration** (30 min)
3. **Rename to ImageSummarizer** (1 hour)
4. **Update all documentation** (1 hour)

Total estimate: **3-4 hours**

## Next Steps

1. Add `ModelContextProtocol.Server` package reference
2. Implement `--mcp` flag with host builder
3. Create `Tools/ImageOcrTools.cs` with MCP tool definitions
4. Test with Claude Desktop
5. Perform rename migration
6. Update all documentation and workflows
