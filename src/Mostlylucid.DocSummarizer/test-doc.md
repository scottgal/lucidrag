# Test Document for DocSummarizer

This is a comprehensive test document to verify the functionality of DocSummarizer.

## Section 1: Introduction

This section introduces the main concepts of the document. We'll cover several key topics including:

- Document processing
- Chunk generation
- Citation tracking
- Summary quality

## Section 2: Technical Details

The technical implementation relies on several components:

1. **Docling** - Converts documents to markdown
2. **Ollama** - Provides LLM inference
3. **Qdrant** - Stores vector embeddings
4. **DocumentChunker** - Splits content by structure

### Subsection 2.1: Architecture

The architecture follows a pipeline pattern:

- Input validation
- Document conversion
- Content chunking
- Parallel processing
- Summary generation

### Subsection 2.2: Performance

Performance metrics are tracked including:

- Processing time
- Coverage score
- Citation rate
- Chunk count

## Section 3: Features

Key features include:

- **Multiple modes**: MapReduce, RAG, Iterative
- **Batch processing**: Process entire directories
- **Output formats**: Console, Text, Markdown, JSON
- **Configuration**: JSON-based with sensible defaults
- **AOT compilation**: Native binaries for fast startup

## Section 4: Conclusion

This document demonstrates the chunking and citation capabilities of DocSummarizer. Each section should be processed as
a separate chunk with appropriate citations.

The tool is designed to be highly configurable while providing excellent out-of-box defaults for common use cases.
