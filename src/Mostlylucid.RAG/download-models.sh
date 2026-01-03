#!/bin/bash

# Download all-MiniLM-L6-v2 ONNX model for semantic search
# This script downloads the model files needed for CPU-friendly embeddings

set -e

echo "================================================"
echo "Downloading Semantic Search Models"
echo "================================================"
echo ""

# Determine the models directory
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
MODELS_DIR="${SCRIPT_DIR}/../Mostlylucid/models"

# Create models directory if it doesn't exist
mkdir -p "$MODELS_DIR"

echo "📁 Models directory: $MODELS_DIR"
echo ""

# Model URLs
MODEL_URL="https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/onnx/model.onnx"
VOCAB_URL="https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/vocab.txt"

MODEL_FILE="$MODELS_DIR/all-MiniLM-L6-v2.onnx"
VOCAB_FILE="$MODELS_DIR/vocab.txt"

# Function to download file with progress
download_file() {
    local url=$1
    local output=$2
    local name=$3

    if [ -f "$output" ]; then
        echo "⏭️  $name already exists, skipping..."
        return 0
    fi

    echo "📥 Downloading $name..."

    if command -v wget &> /dev/null; then
        wget -O "$output" "$url" --progress=bar:force 2>&1 | tail -f -n +6
    elif command -v curl &> /dev/null; then
        curl -L -o "$output" "$url" --progress-bar
    else
        echo "❌ Error: Neither wget nor curl is available"
        exit 1
    fi

    echo "✅ $name downloaded successfully"
    echo ""
}

# Download the model
download_file "$MODEL_URL" "$MODEL_FILE" "ONNX Model (all-MiniLM-L6-v2)"

# Download the vocabulary
download_file "$VOCAB_URL" "$VOCAB_FILE" "Vocabulary file"

# Verify downloads
echo "================================================"
echo "Verifying downloads..."
echo "================================================"
echo ""

if [ -f "$MODEL_FILE" ]; then
    MODEL_SIZE=$(du -h "$MODEL_FILE" | cut -f1)
    echo "✅ Model file: $MODEL_SIZE"
else
    echo "❌ Model file missing!"
    exit 1
fi

if [ -f "$VOCAB_FILE" ]; then
    VOCAB_SIZE=$(du -h "$VOCAB_FILE" | cut -f1)
    echo "✅ Vocabulary file: $VOCAB_SIZE"
else
    echo "❌ Vocabulary file missing!"
    exit 1
fi

echo ""
echo "================================================"
echo "✨ Download complete!"
echo "================================================"
echo ""
echo "Model details:"
echo "  - Name: all-MiniLM-L6-v2"
echo "  - Type: ONNX"
echo "  - Dimensions: 384"
echo "  - Max sequence length: 256 tokens"
echo "  - Use case: Sentence/paragraph embeddings"
echo ""
echo "Next steps:"
echo "  1. Update appsettings.json to enable semantic search"
echo "  2. Start Qdrant: docker-compose -f semantic-search-docker-compose.yml up -d"
echo "  3. Run the application to index your blog posts"
echo ""
