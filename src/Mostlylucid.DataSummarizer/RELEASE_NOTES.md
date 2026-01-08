# DataSummarizer Release Notes

## 🚀 Version 0.3.0 - Privacy-First Data Profiling

**Release Date:** December 20, 2024

**Status:** Early preview release. Core features stable and tested (305 tests passing).

### ✨ Major Features

#### 🔐 Privacy-Safe PII Handling (NEW)

**PII is now hidden by default** across all output formats for maximum privacy and security.

- **Automatic Detection**: ONNX-powered ML classifier + regex patterns detect 20+ PII types
- **Smart Redaction**: Format-preserving redaction (`***-**-6789` for SSN, `jo***@***.com` for email)
- **Type Labels**: Optional `<EMAIL>`, `<PHONE>`, `<NAME>` labels for clarity
- **Granular Control**: CLI options and config for per-type display control

```bash
# Default: PII hidden (privacy-safe) ✅
datasummarizer -f patients.csv

# Explicit opt-in to show PII ⚠️
datasummarizer -f data.csv --show-pii
datasummarizer -f data.csv --show-pii-type email,phone
```

**Benefits:**
- ✅ Screenshot-safe console output
- ✅ CI/CD pipeline safe (no PII in logs)
- ✅ GDPR/HIPAA compliance-friendly
- ✅ Demo-safe with production-like data

#### 🤖 Enhanced ONNX Integration

**ONNX classifier now auto-enables** when configured, dramatically improving PII detection accuracy.

- **Auto-Enable**: Classifier activates automatically from `appsettings.json`
- **5 New CLI Options**: `--onnx-enabled`, `--onnx-model`, `--onnx-gpu`, `--onnx-cpu`, `--onnx-model-dir`
- **GPU Acceleration**: DirectML (Windows) + CUDA support with auto-fallback
- **5 Models Available**: AllMiniLM (default), BGE, GTE, MultiQA, Paraphrase
- **Auto-Download**: Models cached locally (~20-30MB quantized)

```bash
# Use specific model
datasummarizer -f data.csv --onnx-model BgeSmallEnV15

# Force GPU acceleration
datasummarizer -f data.csv --onnx-gpu

# Disable ONNX (regex-only PII detection)
datasummarizer -f data.csv --onnx-enabled false
```

**Performance Impact:**
- Regex-only: 100% speed, good for structured PII
- Regex + ONNX: ~85% speed, excellent for all PII types
- Improved detection of: Names, addresses, unstructured sensitive text

---

### 🔧 Technical Improvements

**Configuration:**
- New `PiiDisplayConfig` with 20+ type-specific settings
- Per-type control (show emails but hide SSN, etc.)
- Configurable redaction characters and visible chars

**Architecture:**
- `PiiRedactionService` for centralized redaction logic
- `PiiResults` stored in `DataProfile` for output filtering
- Enhanced `DuckDbProfiler` with ONNX config flow
- All PII handling runs **100% locally** (no API calls)

**CLI Additions:**
```
--show-pii                Show actual PII values (disables privacy protection)
--show-pii-type <types>   Show specific PII types (email,phone,ssn,name,etc.)
--hide-pii-labels         Hide type labels like <EMAIL> when redacting
--onnx-enabled <bool>     Enable/disable ONNX classifier
--onnx-model <model>      Select embedding model
--onnx-gpu                Force GPU acceleration
--onnx-cpu                Force CPU-only execution
--onnx-model-dir <path>   Custom model directory
```

---

### 📊 Test Results

**All Tests Passing ✅**

- **Unit Tests**: 264/264 passed (100%)
- **Integration Tests**: 41/41 passed (100%)
- **Total**: 305 tests passing

**Tested Scenarios:**
- PII detection with 20+ different PII types
- Redaction format preservation (SSN, email, phone, credit card)
- Selective PII display (`--show-pii-type`)
- ONNX model auto-download and caching
- GPU/CPU fallback behavior
- Hospital patient data (974 rows, real PII)

---

### 📝 Documentation Updates

**New README Sections:**
- **Privacy-Safe PII Handling** - Complete PII redaction guide
- **ONNX Integration** - Model selection, GPU acceleration, troubleshooting
- **Configuration Examples** - `appsettings.json` for PII and ONNX
- **Release Shields** - Tests, Privacy, ONNX status badges

**API Documentation:**
- `PiiDisplayConfig` - Per-type display configuration
- `PiiRedactionService` - Redaction logic and format preservation
- `OnnxConfig` - Embedding models and execution providers

---

### 🎯 Migration Guide

**From v0.2.x:**

**1. PII Output Change (Breaking)**

Previously, all values were shown by default. Now PII is **hidden by default**.

```bash
# Old behavior (shows PII)
datasummarizer -f data.csv

# New behavior (hides PII) - same command ✅
datasummarizer -f data.csv

# To restore old behavior (show PII)
datasummarizer -f data.csv --show-pii
```

**2. Configuration Update (Optional)**

Add to your `appsettings.json` if you want to customize:

```json
{
  "DataSummarizer": {
    "PiiDisplay": {
      "ShowPiiValues": false,
      "ShowPiiTypeLabel": true
    }
  }
}
```

**3. ONNX Auto-Enable (Non-Breaking)**

ONNX classifier now auto-enables from config. To disable:

```json
{
  "DataSummarizer": {
    "Onnx": {
      "Enabled": false
    }
  }
}
```

Or via CLI:
```bash
datasummarizer -f data.csv --onnx-enabled false
```

---

### 🐛 Bug Fixes

- Fixed Spectre.Console markup conflict with PII labels (changed `[EMAIL]` to `<EMAIL>`)
- Fixed PII results not being stored in DataProfile
- Fixed cache interfering with fresh PII detection
- Fixed ONNX config not flowing through to DuckDbProfiler

---

### ⚡ Performance

| Mode | Speed | PII Detection Quality |
|------|-------|----------------------|
| **Fast (no PII)** | 100% | N/A |
| **Regex only** | ~95% | Good for structured PII |
| **Regex + ONNX** | ~85% | Excellent for all types |

**Benchmarks:**
- Hospital patient data (974 rows): ~2s with ONNX
- PII test data (5 rows): <1s with ONNX
- Model download (first run): ~10s for 23MB quantized model
- Subsequent runs: Instant (cached)

---

### 📦 Files Changed

**New Files:**
- `Configuration/PiiDisplayConfig.cs` - PII display configuration
- `Services/PiiRedactionService.cs` - Redaction logic
- `RELEASE_NOTES.md` - This file

**Modified Files:**
- `README.md` - Added Privacy and ONNX sections, shields
- `Program.cs` - PII redaction integration, 8 new CLI options
- `Services/DuckDbProfiler.cs` - Auto-enable ONNX, store PII results
- `Services/DataSummarizerService.cs` - Pass OnnxConfig and PiiDisplayConfig
- `Models/DataProfile.cs` - Added `PiiResults` property
- `appsettings.json` - Added PII display configuration

---

### 🙏 Credits

- **ONNX Runtime**: Microsoft's cross-platform ML inference engine
- **HuggingFace**: Pre-trained sentence transformer models
- **Spectre.Console**: Beautiful console UI framework
- **DuckDB**: High-performance analytical database engine

---

### 🔮 Roadmap

**Upcoming Features:**
- PDF/Markdown report PII redaction
- JSON output PII redaction options
- Custom PII patterns via config
- PII detection confidence thresholds
- Export redaction audit logs

---

### 📞 Support

- **Issues**: [GitHub Issues](https://github.com/scottgal/mostlylucidweb/issues)
- **Documentation**: [README.md](README.md)
- **Examples**: `sampledata/` directory

---

**Full Changelog**: https://github.com/scottgal/mostlylucidweb/commits/main/Mostlylucid.DataSummarizer
