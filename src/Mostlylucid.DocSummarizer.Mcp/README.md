# DocSummarizer MCP Server v3.0.0

MCP (Model Context Protocol) server that exposes local Ollama LLM capabilities as tools for AI agents.

## What's New in v3.0.0

- **Resilient Embeddings**: Automatic retry with jitter backoff for embedding failures
- **Long Text Support**: Texts >1000 chars are split into chunks and averaged
- **Windows Stability**: Fixed Ollama wsarecv connection issues

## Tools Available

| Tool | Description |
|------|-------------|
| `check_ollama` | Check if Ollama is available and list installed models |
| `generate_text` | Generate text using a local Ollama LLM |
| `generate_embedding` | Generate vector embeddings for semantic search |
| `calculate_similarity` | Calculate cosine similarity between two texts |
| `summarize_text` | Summarize provided text |
| `summarize_file` | Read and summarize a text file |
| `ask_about_text` | Answer questions about provided text |

## Prerequisites

- [Ollama](https://ollama.ai/) running locally on port 11434
- Models pulled: `ollama pull llama3.2:3b` and `ollama pull nomic-embed-text`

## Running the Server

```bash
# From the repository root
dotnet run --project Mostlylucid.DocSummarizer.Mcp/Mostlylucid.DocSummarizer.Mcp.csproj
```

## Configuration

Environment variables:
- `OLLAMA_MODEL` - Default LLM model (default: `llama3.2:3b`)
- `OLLAMA_EMBED_MODEL` - Default embedding model (default: `nomic-embed-text`)
- `OLLAMA_BASE_URL` - Ollama API URL (default: `http://localhost:11434`)

## OpenCode Integration

Add to `.opencode/mcp.json`:

```json
{
  "mcpServers": {
    "docsummarizer": {
      "command": "dotnet",
      "args": ["run", "--project", "Mostlylucid.DocSummarizer.Mcp/Mostlylucid.DocSummarizer.Mcp.csproj"],
      "env": {
        "OLLAMA_MODEL": "llama3.2:3b",
        "OLLAMA_EMBED_MODEL": "nomic-embed-text"
      }
    }
  }
}
```

## Example Usage

Once connected, the AI agent can use tools like:

```
// Check Ollama availability
check_ollama()

// Generate text
generate_text(prompt: "Explain quantum computing in simple terms")

// Generate embeddings
generate_embedding(text: "Hello world")

// Calculate semantic similarity
calculate_similarity(text1: "Hello", text2: "Hi there")

// Summarize text
summarize_text(text: "Long article...", length: "brief")

// Summarize a file
summarize_file(filePath: "./README.md", length: "medium")
```
