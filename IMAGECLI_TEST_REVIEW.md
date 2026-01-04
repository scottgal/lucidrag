# LucidRAG ImageCli Test Coverage Review

**Date:** 2026-01-04
**Reviewer:** Claude (Automated Review)
**Scope:** Recent features - Caching, Ordering, JSON-LD Export

## Executive Summary

⚠️ **CRITICAL:** No dedicated test project exists for `LucidRAG.ImageCli`. Core features lack test coverage.

**Test Coverage Status:**
- ✅ **Good:** Underlying image analysis (BlurAnalyzer, ColorAnalyzer, EdgeAnalyzer, etc.)
- ✅ **Good:** OllamaVisionClient, TesseractOcrEngine (basic tests exist)
- ❌ **MISSING:** EscalationService (0% coverage)
- ❌ **MISSING:** ImageBatchProcessor (0% coverage)
- ❌ **MISSING:** SignalDatabase caching (0% coverage - **CRITICAL BUG EXISTS**)
- ❌ **MISSING:** BatchCommand ordering/sorting (0% coverage)
- ❌ **MISSING:** JSON-LD export validation (0% coverage)

## Recent Features Requiring Tests

### 1. Content-Based Caching (EscalationService)

**Status:** ⚠️ **HAS KNOWN BUG** - Cache deserialization returns zeros

**What Works:**
- ✅ xxhash64 computation
- ✅ SHA256 computation
- ✅ Cache hit detection (logs show "Cache hit for...")
- ✅ Filename-independent cache keys

**What's Broken:**
- ❌ Cache data deserialization - returns `ImageProfile` with all zeros
- ❌ `ConvertToImageProfile(DynamicImageProfile)` may not be reading signals correctly

**Tests Needed:**
```csharp
// Unit Tests
- Test_ComputeXxHash64_ProducesSameHashForSameContent()
- Test_ComputeXxHash64_ProducesDifferentHashForDifferentContent()
- Test_ComputeSha256_ProducesCorrectHash()
- Test_CacheHit_ReturnsCachedProfile()
- Test_CacheMiss_PerformsFullAnalysis()
- Test_CacheKey_FilenameIndependent() // Rename test
- Test_ConvertToImageProfile_PreservesAllFields() // Fix deserialization bug
- Test_ConvertToDynamicProfile_CreatesCorrectSignals()
- Test_RoundTrip_ImageProfileToDynamicAndBack() // Critical!

// Integration Tests
- Test_AnalyzeSameImageTwice_SecondRunUsesCache()
- Test_RenamedFile_HitsCache()
- Test_ModifiedFile_BypassesCache()
- Test_ConcurrentAccess_NoDatabaseLocking()
```

**Critical Bug to Fix:**
```csharp
// File: EscalationService.cs:342-376
// ConvertToImageProfile returns zeros for cached data
// Likely issue: DynamicImageProfile.GetValue<T>() not deserializing from DB correctly
```

### 2. SignalDatabase Caching

**Status:** ❌ **NO TESTS AT ALL** - Core caching infrastructure untested

**Tests Needed:**
```csharp
// Unit Tests
- Test_StoreProfile_SavesAllSignals()
- Test_LoadProfile_RetrievesAllSignals()
- Test_LoadProfile_NonExistentHash_ReturnsNull()
- Test_GetStatistics_ReturnsCorrectCounts()
- Test_StoreFeedback_SavesFeedbackCorrectly()
- Test_RoundTrip_StoreAndLoad_PreservesData() // Critical for bug fix

// Integration Tests
- Test_ConcurrentWrites_NoDataCorruption()
- Test_DatabaseCreation_CreatesAllTables()
- Test_Migration_HandlesSchemaChanges() // Future-proofing
```

**Investigation Needed:**
```sql
-- Check what's actually being stored in the database
SELECT * FROM signals WHERE sha256 = '5233004d415fd9363aa0694461acb87de79bd4fd1e70ea19e0df3156f43a4264';

-- Verify signal key names match between storage and retrieval
SELECT DISTINCT signal_key FROM signals;
```

### 3. Batch Ordering/Sorting

**Status:** ✅ **Manually Verified** but ❌ **NO AUTOMATED TESTS**

**Verified Working:**
- ✅ `--order-by sharpness --descending` - Sorts correctly (2856 → 624 → 332...)
- ✅ Message shows: "Sorted by sharpness (descending)"

**Tests Needed:**
```csharp
// Unit Tests
- Test_SortBySharpness_Ascending()
- Test_SortBySharpness_Descending()
- Test_SortByResolution_CalculatesPixelCount()
- Test_SortByColor_AlphabeticalByName()
- Test_SortByBrightness_UsesMeanLuminance()
- Test_SortBySaturation_UsesMeanSaturation()
- Test_SortByType_GroupsByDetectedType()
- Test_SortByTextScore_UsesTextLikeliness()
- Test_SortUnknownProperty_ReturnsOriginalOrder()
- Test_SortWithNullProfiles_HandlesGracefully()

// Integration Tests
- Test_BatchCommand_OrderBy_SortsResults()
- Test_BatchCommand_OrderBy_DescendingFlag()
```

### 4. JSON-LD Export

**Status:** ✅ **Manually Verified** but ❌ **NO AUTOMATED TESTS**

**Verified Working:**
- ✅ Generates valid JSON structure
- ✅ Includes @context with schema.org mappings
- ✅ Exports complete fingerprint data (when cache works)
- ✅ Includes dominant colors, LLM caption, all metrics

**Tests Needed:**
```csharp
// Unit Tests
- Test_ExportToJsonLd_ValidJsonStructure()
- Test_ExportToJsonLd_ContainsContext()
- Test_ExportToJsonLd_MapsToSchemaOrg()
- Test_ExportToJsonLd_IncludesAllFingerprints()
- Test_ExportToJsonLd_HandlesNullValues()
- Test_ExportToJsonLd_EscapesSpecialCharacters()
- Test_ExportToJsonLd_ValidISODates()

// Integration Tests
- Test_BatchCommand_ExportJsonLd_CreatesFile()
- Test_ExportedJsonLd_ValidatesAgainstJsonLdSpec()
- Test_ExportedJsonLd_Parseable()
```

**JSON-LD Validation:**
```bash
# Validate against JSON-LD playground
curl -X POST https://json-ld.org/playground/ -d @fingerprints.jsonld

# Or use jsonld library
npm install -g jsonld
jsonld format -q fingerprints.jsonld
```

### 5. EscalationService

**Status:** ❌ **NO TESTS** - Core orchestration service

**Tests Needed:**
```csharp
// Unit Tests
- Test_ShouldAutoEscalate_LowConfidence_ReturnsTrue()
- Test_ShouldAutoEscalate_BlurryImage_ReturnsTrue()
- Test_ShouldAutoEscalate_HighTextContent_ReturnsTrue()
- Test_ShouldAutoEscalate_ComplexDiagram_ReturnsTrue()
- Test_ShouldAutoEscalate_HighQualityPhoto_ReturnsFalse()
- Test_AnalyzeWithEscalation_ForceEscalate_CallsVisionLlm()
- Test_AnalyzeWithEscalation_NoEscalation_SkipsVisionLlm()
- Test_AnalyzeBatchAsync_ProcessesAllImages()
- Test_AnalyzeBatchAsync_ReportsProgress()
- Test_AnalyzeBatchAsync_HandlesErrors()
- Test_AnalyzeBatchAsync_RespectsCancellation()

// Integration Tests
- Test_EscalationPipeline_EndToEnd()
- Test_VisionLlmUnavailable_GracefulDegradation()
```

### 6. ImageBatchProcessor

**Status:** ❌ **NO TESTS** - Parallel processing untested

**Tests Needed:**
```csharp
// Unit Tests
- Test_FindImageFiles_MatchesGlobPattern()
- Test_FindImageFiles_Recursive()
- Test_FindImageFiles_NonRecursive()
- Test_ProcessBatch_ReturnsCorrectCount()
- Test_ProcessBatch_AppliesFilter()
- Test_ProcessBatch_HandlesErrors()
- Test_ExportToCsv_CreatesFile()

// Integration Tests
- Test_ParallelProcessing_NoRaceConditions()
- Test_ParallelProcessing_CorrectWorkerCount()
- Test_ProgressReporting_AccurateCounts()
```

## Integration Test Scenarios

### End-to-End Workflows

1. **Complete Batch Analysis with All Features**
```csharp
[Fact]
public async Task E2E_BatchAnalysis_WithOrdering_JsonLdExport_Caching()
{
    // First run: Fresh analysis
    var result1 = await RunBatch(
        testImagesDir,
        orderBy: "sharpness",
        exportJsonLd: "output1.jsonld");

    // Verify analysis completed
    Assert.All(result1.Results, r => Assert.NotNull(r.Profile));

    // Verify ordering
    AssertSortedBySharpness(result1.Results, descending: false);

    // Verify JSON-LD export
    var jsonLd = await File.ReadAllTextAsync("output1.jsonld");
    var doc = JsonDocument.Parse(jsonLd);
    Assert.Equal("lucidrag:ImageFingerprintCollection", doc.RootElement.GetProperty("type").GetString());

    // Second run: Should use cache
    var result2 = await RunBatch(testImagesDir);
    Assert.All(result2.Results, r => Assert.True(r.FromCache));

    // Verify cached data matches original
    AssertProfilesEqual(result1.Results[0].Profile, result2.Results[0].Profile);
}
```

2. **Ordering by Different Properties**
```csharp
[Theory]
[InlineData("sharpness", false)]
[InlineData("sharpness", true)]
[InlineData("resolution", false)]
[InlineData("brightness", false)]
[InlineData("color", false)]
[InlineData("type", false)]
public async Task E2E_Ordering_VerifySort(string orderBy, bool descending)
{
    var result = await RunBatch(testImagesDir, orderBy, descending);
    AssertCorrectOrder(result.Results, orderBy, descending);
}
```

3. **Cache Invalidation**
```csharp
[Fact]
public async Task E2E_Cache_Invalidation_OnFileChange()
{
    // Analyze original
    var result1 = await AnalyzeImage("test.png");

    // Modify file
    ModifyImageFile("test.png");

    // Re-analyze - should bypass cache
    var result2 = await AnalyzeImage("test.png");

    Assert.NotEqual(result1.Profile.Sha256, result2.Profile.Sha256);
    Assert.False(result2.FromCache);
}
```

## Test Infrastructure Needed

### 1. Test Project Setup
```bash
dotnet new xunit -n LucidRAG.ImageCli.Tests
dotnet sln add src/LucidRAG.ImageCli.Tests/LucidRAG.ImageCli.Tests.csproj
dotnet add src/LucidRAG.ImageCli.Tests reference src/LucidRAG.ImageCli
```

### 2. Test Fixtures
```csharp
public class TestImageFixture : IDisposable
{
    public string TestImagesDirectory { get; }
    public List<string> TestImagePaths { get; }

    public TestImageFixture()
    {
        // Create temp directory with known test images
        // Copy from Mostlylucid.DocSummarizer.Images.Tests/TestImages
    }
}

public class InMemoryCacheFixture
{
    public ISignalDatabase Database { get; }

    public InMemoryCacheFixture()
    {
        // Use in-memory SQLite for fast tests
        Database = new SignalDatabase(":memory:");
    }
}
```

### 3. Mocks
```csharp
public class MockVisionLlmService : IVisionLlmService
{
    public int CallCount { get; private set; }
    public Queue<string> CannedResponses { get; } = new();

    public async Task<string> AnalyzeImageAsync(string path, CancellationToken ct)
    {
        CallCount++;
        return CannedResponses.Dequeue();
    }
}
```

## Priority Test List (Immediate)

**P0 - Critical (Fix Bugs):**
1. ✅ SignalDatabase round-trip test (fix deserialization bug)
2. ✅ EscalationService cache conversion tests
3. ✅ Integration test: Analyze → Cache → Load → Verify equality

**P1 - High (Core Features):**
4. ⬜ Ordering/sorting unit tests (all 8 properties)
5. ⬜ JSON-LD structure validation tests
6. ⬜ EscalationService auto-escalation logic tests
7. ⬜ Batch processing with parallel workers

**P2 - Medium (Edge Cases):**
8. ⬜ Null handling in sort methods
9. ⬜ Concurrent cache access
10. ⬜ Glob pattern matching
11. ⬜ Progress reporting accuracy

**P3 - Low (Nice to Have):**
12. ⬜ Performance benchmarks
13. ⬜ Memory leak tests
14. ⬜ Large batch processing (1000+ images)

## Known Issues to Test

### Cache Deserialization Bug
**Symptom:** `LoadProfileAsync` returns `DynamicImageProfile`, but `ConvertToImageProfile` produces zeros.

**Root Cause Investigation:**
```csharp
// Check if signals are stored correctly
var dynamicProfile = await _signalDatabase.LoadProfileAsync(sha256, ct);
foreach (var signal in dynamicProfile.GetAllSignals())
{
    Console.WriteLine($"{signal.Key} = {signal.Value} (Type: {signal.Value?.GetType()})");
}

// Check if GetValue<T> works
var width = dynamicProfile.GetValue<int>("identity.width");
Console.WriteLine($"Width: {width}"); // Should not be 0
```

**Hypothesis:**
- Signals stored as JSON but deserialized as `object`
- `GetValue<T>` may need type casting logic
- Signal keys may have whitespace or encoding issues

## Test Execution Plan

### Phase 1: Bug Fixes (Day 1)
1. Create `LucidRAG.ImageCli.Tests` project
2. Add `SignalDatabaseTests.cs` - round-trip test
3. Debug and fix deserialization bug
4. Add `EscalationServiceCachingTests.cs`
5. Verify cache works end-to-end

### Phase 2: Feature Coverage (Day 2)
6. Add `BatchCommandSortingTests.cs` (all 8 sort modes)
7. Add `JsonLdExportTests.cs` (structure validation)
8. Add `ImageBatchProcessorTests.cs` (parallel processing)

### Phase 3: Integration Tests (Day 3)
9. Add `E2EWorkflowTests.cs`
10. Add performance benchmarks
11. Run full test suite against real Ollama instance

## Metrics Target

- **Unit Test Coverage:** 80%+ for CLI services
- **Integration Test Coverage:** 90%+ for critical paths
- **Test Execution Time:** <30 seconds (unit), <2 minutes (integration)
- **Zero Known Bugs:** Fix cache deserialization before release

## Recommendations

1. **IMMEDIATE:** Fix cache deserialization bug (blocks production use)
2. **HIGH:** Create test project and add P0/P1 tests
3. **MEDIUM:** Add JSON-LD schema validation
4. **LOW:** Performance testing with large datasets

## Tools Needed

- xUnit (test framework) ✅
- Moq (mocking) ✅ (already in Images.Tests)
- FluentAssertions (better assertions)
- BenchmarkDotNet (performance testing)
- json-ld library (JSON-LD validation)

## Review Conclusion

**Status:** ⚠️ **NOT PRODUCTION READY**

**Blockers:**
1. Cache deserialization bug must be fixed
2. Zero test coverage for new features
3. SignalDatabase has no tests

**Next Steps:**
1. Fix cache bug (P0)
2. Create test project
3. Add critical tests
4. Verify all features work correctly
5. Document testing strategy
