# LucidRAG Documentation Index

Complete documentation for the LucidRAG project and ImageSummarizer OCR tool.

## Quick Links

- **Main README**: [README.md](README.md) - Project overview and quick start
- **ImageSummarizer README**: [src/Mostlylucid.ImageSummarizer.Cli/README.md](src/Mostlylucid.ImageSummarizer.Cli/README.md)
- **Command Reference**: [src/Mostlylucid.ImageSummarizer.Cli/COMMAND-REFERENCE.md](src/Mostlylucid.ImageSummarizer.Cli/COMMAND-REFERENCE.md)
- **MCP Enhancements**: [MCP-ENHANCEMENTS-SUMMARY.md](MCP-ENHANCEMENTS-SUMMARY.md)

## ImageSummarizer Documentation

### Core Documentation
1. **[README.md](src/Mostlylucid.ImageSummarizer.Cli/README.md)** - Main documentation
   - Installation instructions
   - Basic usage and examples
   - Pipeline descriptions (Simple, Advanced, Quality)
   - MCP server integration
   - Output formats
   - Troubleshooting

2. **[COMMAND-REFERENCE.md](src/Mostlylucid.ImageSummarizer.Cli/COMMAND-REFERENCE.md)** - Complete command reference
   - All command-line flags and options
   - Pipeline selection guide
   - Output format examples
   - MCP configuration details
   - Usage patterns and recipes
   - Performance tips

### MCP Server Mode
3. **[MCP-IMPLEMENTATION-SUMMARY.md](MCP-IMPLEMENTATION-SUMMARY.md)** - Original MCP implementation
   - 4 core MCP tools (OCR, quality, pipelines, batch)
   - Claude Desktop configuration
   - MCP protocol details
   - Testing results

4. **[MCP-ENHANCEMENTS-SUMMARY.md](MCP-ENHANCEMENTS-SUMMARY.md)** - **NEW** Enhanced MCP features
   - 5 new MCP tools (summarize GIF, caption, description, templates)
   - Template system with 9 predefined templates
   - Variable substitution and operators
   - 31 available variables
   - Complete implementation details

### Rename Documentation
5. **[IMAGECLI-TO-IMAGESUMMARIZER-RENAME-SUMMARY.md](IMAGECLI-TO-IMAGESUMMARIZER-RENAME-SUMMARY.md)**
   - Complete rename process from ImageCli to ImageSummarizer
   - Naming pattern changes
   - All files affected
   - Build and test verification

### Related Documentation
6. **[GIF-TEXT-EXTRACTION-IMPLEMENTATION.md](GIF-TEXT-EXTRACTION-IMPLEMENTATION.md)** - GIF OCR implementation details
7. **[OCR-PIPELINE-RESULTS.md](OCR-PIPELINE-RESULTS.md)** - Pipeline performance testing
8. **[OCR-OPTIMIZATION-RESULTS.md](OCR-OPTIMIZATION-RESULTS.md)** - Optimization benchmarks

## LucidRAG Web Application

### Getting Started
- **[README.md](README.md)** - Project overview, quick start, configuration
- **[CLAUDE.md](CLAUDE.md)** - Claude Code integration guide
- **[src/LucidRAG/README.md](src/LucidRAG/README.md)** - Web application details

### Architecture & Design
- **[ARCHITECTURE_VISION.md](ARCHITECTURE_VISION.md)** - System architecture and design goals
- **[SOLID_REVIEW_AND_TESTS.md](SOLID_REVIEW_AND_TESTS.md)** - Code quality review
- **[QDRANT-MULTI-VECTOR-IMPLEMENTATION.md](QDRANT-MULTI-VECTOR-IMPLEMENTATION.md)** - Vector store implementation

### GraphRAG Features
- **[src/Mostlylucid.GraphRag/](src/Mostlylucid.GraphRag/)** - Entity extraction and knowledge graphs
- **[ML-LLM-FEATURES.md](ML-LLM-FEATURES.md)** - Machine learning and LLM features

### Development Documentation
- **[RECENT-IMPROVEMENTS.md](RECENT-IMPROVEMENTS.md)** - Latest improvements and changes
- **[SESSION-SUMMARY.md](SESSION-SUMMARY.md)** - Development session summaries
- **[demo/DEMO_GUIDE.md](demo/DEMO_GUIDE.md)** - Demo and presentation guide

## Component Documentation

### DocSummarizer.Core
- **[src/Mostlylucid.DocSummarizer.Core/README.md](src/Mostlylucid.DocSummarizer.Core/README.md)**
  - Document processing
  - ONNX embeddings
  - BM25 search

### DocSummarizer.Images
- **[src/Mostlylucid.DocSummarizer.Images/README.md](src/Mostlylucid.DocSummarizer.Images/README.md)**
  - Image analysis waves
  - OCR engines
  - Vision LLM integration

- **[src/Mostlylucid.DocSummarizer.Images/PIPELINES.md](src/Mostlylucid.DocSummarizer.Images/PIPELINES.md)**
  - Pipeline configuration
  - Wave orchestration
  - Custom pipeline creation

- **[src/Mostlylucid.DocSummarizer.Images/ANALYZERS.md](src/Mostlylucid.DocSummarizer.Images/ANALYZERS.md)**
  - Available analyzers
  - Signal system
  - Quality metrics

### RAG System
- **[src/Mostlylucid.RAG/README.md](src/Mostlylucid.RAG/README.md)**
  - Vector store abstraction
  - DuckDB and Qdrant backends
  - Multi-vector support

## GitHub Workflows

### Build & Test
- **[.github/workflows/build.yml](.github/workflows/build.yml)**
  - Main CI/CD pipeline
  - Runs on PR and push to main
  - PostgreSQL service for tests
  - Browser tests filtered out (`Category!=Browser`)

### Releases
- **[.github/workflows/release-imagesummarizer.yml](.github/workflows/release-imagesummarizer.yml)**
  - Multi-platform builds (Windows, Linux, macOS)
  - x64 and ARM64 support
  - Tag pattern: `imagesummarizer-v*.*.*`
  - Includes all 9 MCP tools in release notes

- **[.github/workflows/release-lucidrag.yml](.github/workflows/release-lucidrag.yml)**
  - Docker multi-arch images
  - Tag pattern: `lucidrag-v*.*.*`

- **[.github/workflows/release-lucidrag-cli.yml](.github/workflows/release-lucidrag-cli.yml)**
  - CLI binary releases

- **[.github/workflows/publish-docsummarizer-nuget.yml](.github/workflows/publish-docsummarizer-nuget.yml)**
  - NuGet package publishing
  - Tag pattern: `docsumv*.*.*`

## Configuration Files

### ImageSummarizer Templates
- **[src/Mostlylucid.ImageSummarizer.Cli/Config/output-templates.json](src/Mostlylucid.ImageSummarizer.Cli/Config/output-templates.json)**
  - 9 predefined templates
  - Variable reference
  - Operator documentation

### Pipeline Configurations
- **[src/Mostlylucid.DocSummarizer.Images/Config/pipelines.json](src/Mostlylucid.DocSummarizer.Images/Config/pipelines.json)**
  - Simple, Advanced, Quality pipelines
  - Wave configurations
  - Custom pipeline templates

## Testing Documentation

### Test Projects
- **src/LucidRAG.Tests/** - Main application integration tests (uses PostgreSQL service container)
- **src/LucidRAG.ImageCli.Tests/** - ImageSummarizer CLI tests (uses local SQLite)
- **src/Mostlylucid.DocSummarizer.Images.Tests/** - Image analysis tests
- **src/Mostlylucid.DocSummarizer.Core.Tests/** - Core library tests

### Test Notes
- All tests use **local services only** (no external APIs required)
- PostgreSQL tests use GitHub Actions service containers
- SQLite tests use temp files
- Browser tests excluded in CI with `--filter "Category!=Browser"`

## MCP Tools Reference

### Core OCR Tools (Original 4)
1. **extract_text_from_image** - OCR with configurable pipeline
2. **analyze_image_quality** - Fast quality metrics
3. **list_ocr_pipelines** - Pipeline discovery
4. **batch_extract_text** - Multi-file processing

### Content Generation Tools (NEW - 3)
5. **summarize_animated_gif** - Motion-aware GIF summaries
6. **generate_caption** - Accessibility captions (WCAG-compliant)
7. **generate_detailed_description** - Comprehensive image analysis

### Template System Tools (NEW - 2)
8. **analyze_with_template** - Format with 9 predefined templates
9. **list_output_templates** - Template discovery

### Template System
- **9 Templates**: social_media, accessibility, seo, technical_report, json_structured, markdown_blog, content_moderation, animated_gif_summary, custom
- **31 Variables**: identity.*, colors.*, text.*, motion.*, quality.*, composition.*, llm_summary, alt_text_context
- **3 Operators**: Fallback (`|`), Ternary (`?:`), Comparison (`><==`)

## Claude Desktop Integration

### Configuration
Add to `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "image-analysis": {
      "command": "imagesummarizer",
      "args": ["--mcp"],
      "env": {
        "OCR_PIPELINE": "advancedocr",
        "OCR_LANGUAGE": "en_US"
      }
    }
  }
}
```

### Usage Examples
- **Summarize GIF**: "Summarize this GIF with motion details: F:/Gifs/animation.gif"
- **Generate Caption**: "Generate an accessible caption for this image"
- **Format Output**: "Format this image analysis for social media"
- **List Templates**: "What output templates are available?"

## Build Commands

```bash
# Build entire solution
dotnet build LucidRAG.sln

# Build ImageSummarizer only
dotnet build src/Mostlylucid.ImageSummarizer.Cli/Mostlylucid.ImageSummarizer.Cli.csproj

# Run tests (excluding browser tests)
dotnet test --filter "Category!=Browser"

# Run ImageSummarizer
dotnet run --project src/Mostlylucid.ImageSummarizer.Cli/Mostlylucid.ImageSummarizer.Cli.csproj -- image.gif

# Start MCP server
dotnet run --project src/Mostlylucid.ImageSummarizer.Cli/Mostlylucid.ImageSummarizer.Cli.csproj -- --mcp
```

## Release Process

### ImageSummarizer Releases
```bash
# Create and push tag
git tag imagesummarizer-v1.0.0
git push origin imagesummarizer-v1.0.0

# GitHub Actions will:
# 1. Build for 6 platforms (Windows, Linux, macOS × x64, ARM64)
# 2. Create artifacts (.zip for Windows, .tar.gz for Linux/macOS)
# 3. Publish GitHub release with all MCP tools documented
```

### LucidRAG Releases
```bash
# Create and push tag
git tag lucidrag-v1.0.0
git push origin lucidrag-v1.0.0

# GitHub Actions will:
# 1. Run tests with PostgreSQL
# 2. Build Docker images (amd64, arm64)
# 3. Push to Docker Hub
# 4. Create GitHub release
```

## Support & Troubleshooting

### Common Issues

**ImageSummarizer:**
- **Dictionary not available**: Auto-downloads on first use (requires internet)
- **Low OCR quality**: Try `--pipeline quality`
- **No text extracted**: Check `text.text_likeliness` signal

**LucidRAG:**
- **PostgreSQL connection**: Check connection string in appsettings.json
- **Ollama not found**: Ensure Ollama is running on localhost:11434
- **Upload fails**: Check file size limits and supported formats

### Documentation Issues
- Report issues at: https://github.com/scottgal/lucidrag/issues
- For ImageSummarizer: Tag with `imagesummarizer` label
- For MCP integration: Tag with `mcp` label

## License

All projects: MIT License - see [LICENSE](LICENSE)

---

**Last Updated**: 2026-01-04
**Documentation Version**: 1.0
**ImageSummarizer Version**: 1.0.0 (with MCP enhancements)
**LucidRAG Version**: Latest from main branch
