using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Mostlylucid.DocSummarizer.Config;
using UglyToad.PdfPig;

namespace Mostlylucid.DocSummarizer.Services;

/// <summary>
/// Progress info for document conversion
/// </summary>
public class ConversionProgress
{
    public int TotalChunks { get; set; }
    public int CompletedChunks { get; set; }
    public int CurrentWave { get; set; }
    public int TotalWaves { get; set; }
    public string Status { get; set; } = "";
    public double Percent => TotalChunks > 0 ? (double)CompletedChunks / TotalChunks * 100 : 0;
}

public class DoclingClient : IDisposable
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(20);

    private readonly string _baseUrl;
    private readonly DoclingConfig _config;
    private readonly HttpClient _http;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _timeout;
    private bool? _hasGpu;
    
    /// <summary>
    /// Progress callback - receives updates during conversion
    /// </summary>
    public Action<ConversionProgress>? OnProgress { get; set; }
    
    /// <summary>
    /// Chunk completion callback - fires when each chunk's markdown is ready.
    /// Use this for pipelined processing (e.g., start embedding while other chunks convert).
    /// Parameters: (chunkIndex, startPage, endPage, markdown)
    /// </summary>
    public Action<int, int, int, string>? OnChunkComplete { get; set; }
    
    /// <summary>
    /// Whether Docling is running with GPU acceleration (detected on first use)
    /// </summary>
    public bool? HasGpu => _hasGpu;

    public DoclingClient(DoclingConfig? config = null)
    {
        _config = config ?? new DoclingConfig();
        _baseUrl = _config.BaseUrl;
        _timeout = TimeSpan.FromSeconds(_config.TimeoutSeconds);
        _pollInterval = TimeSpan.FromSeconds(2);
        _http = new HttpClient { Timeout = _timeout + TimeSpan.FromMinutes(1) };
    }

    public DoclingClient(string baseUrl, TimeSpan? timeout = null)
        : this(new DoclingConfig { BaseUrl = baseUrl, TimeoutSeconds = (int)(timeout?.TotalSeconds ?? 1200) })
    {
    }

    public void Dispose() => _http.Dispose();
    
    private void Report(int completed, int total, int wave, int totalWaves, string status)
    {
        OnProgress?.Invoke(new ConversionProgress
        {
            CompletedChunks = completed,
            TotalChunks = total,
            CurrentWave = wave,
            TotalWaves = totalWaves,
            Status = status
        });
    }

    public async Task<string> ConvertAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Document not found: {filePath}");

        if (filePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && _config.EnableSplitProcessing)
            return await ConvertPdfWithSplitProcessingAsync(filePath, cancellationToken);

        // DOCX: Always use standard (single-pass) conversion.
        // Docling doesn't handle split/chunked DOCX well - returns empty text for chunks.
        return await ConvertStandardAsync(filePath, cancellationToken);
    }

    private async Task<string> ConvertStandardAsync(string filePath, CancellationToken cancellationToken)
    {
        return await ConvertStandardAsyncCore(filePath, _config.PdfBackend, cancellationToken, allowFallback: true);
    }

    private async Task<string> ConvertStandardAsyncCore(string filePath, string? backend, CancellationToken cancellationToken, bool allowFallback)
    {
        Report(0, 1, 1, 1, "Starting conversion...");
        
        var taskId = await StartConversionAsync(filePath, null, null, backend, cancellationToken);
        var result = await WaitForCompletionAsync(taskId, cancellationToken);

        Report(1, 1, 1, 1, "Conversion complete");

        if (allowFallback && filePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && IsGarbageText(result))
        {
            // Try OCR/docling backend if enabled
            if (_config.EnableOcrFallback && !string.IsNullOrWhiteSpace(_config.OcrPdfBackend) && !string.Equals(_config.OcrPdfBackend, backend, StringComparison.OrdinalIgnoreCase))
            {
                Report(1, 1, 1, 1, "Garbled text detected - retrying with OCR backend...");
                try
                {
                    var ocrResult = await ConvertStandardAsyncCore(filePath, _config.OcrPdfBackend, cancellationToken, allowFallback: false);
                    if (!string.IsNullOrWhiteSpace(ocrResult) && !IsGarbageText(ocrResult))
                        return ocrResult;
                }
                catch { }
            }

            Report(1, 1, 1, 1, "Trying PdfPig fallback...");
            try
            {
                var pdfPigResult = ExtractWithPdfPig(filePath);
                if (!string.IsNullOrWhiteSpace(pdfPigResult) && !IsGarbageText(pdfPigResult))
                    return pdfPigResult;
            }
            catch { }
        }

        return result;
    }

    private async Task<string> ConvertPdfWithSplitProcessingAsync(string filePath, CancellationToken cancellationToken)
    {
        int totalPages;
        try
        {
            totalPages = GetPdfPageCount(filePath);
            Report(0, 1, 0, 1, $"PDF: {totalPages} pages");
        }
        catch (Exception ex)
        {
            Report(0, 1, 0, 1, $"Could not read PDF: {ex.Message}");
            return await ConvertStandardAsync(filePath, cancellationToken);
        }

        // Use MinPagesForSplit if set, otherwise fall back to PagesPerChunk
        var minPagesForSplit = _config.MinPagesForSplit > 0 ? _config.MinPagesForSplit : _config.PagesPerChunk;
        if (totalPages <= minPagesForSplit)
        {
            Report(0, 1, 0, 1, $"PDF ({totalPages} pages) - standard conversion");
            return await ConvertStandardAsync(filePath, cancellationToken);
        }

        var pagesPerChunk = _config.PagesPerChunk;
        var maxConcurrent = _config.MaxConcurrentChunks;
        var numChunks = (int)Math.Ceiling((double)totalPages / pagesPerChunk);
        var totalWaves = (int)Math.Ceiling((double)numChunks / maxConcurrent);

        var allChunks = new List<PdfChunkTask>();
        for (var i = 0; i < numChunks; i++)
        {
            var startPage = i * pagesPerChunk + 1;
            var endPage = Math.Min(startPage + pagesPerChunk - 1, totalPages);
            allChunks.Add(new PdfChunkTask(i, startPage, endPage, ""));
        }

        Report(0, numChunks, 0, totalWaves, $"Processing {numChunks} chunks ({pagesPerChunk} pages each)");

        var startTime = DateTime.UtcNow;
        var waveNumber = 0;
        var completedChunks = 0;

        for (var waveStart = 0; waveStart < allChunks.Count; waveStart += maxConcurrent)
        {
            cancellationToken.ThrowIfCancellationRequested();
            waveNumber++;

            var waveChunks = allChunks.Skip(waveStart).Take(maxConcurrent).ToList();
            var waveDesc = string.Join(", ", waveChunks.Select(c => $"p{c.StartPage}-{c.EndPage}"));
            
            Report(completedChunks, numChunks, waveNumber, totalWaves, $"Wave {waveNumber}/{totalWaves}: {waveDesc}");

            // Submit this wave
            foreach (var chunk in waveChunks)
            {
                try
                {
                    chunk.TaskId = await StartConversionAsync(filePath, chunk.StartPage, chunk.EndPage, _config.PdfBackend, cancellationToken);
                }
                catch
                {
                    chunk.IsFailed = true;
                }
            }

            // Poll for this wave to complete
            var pendingChunks = waveChunks.Where(c => !string.IsNullOrEmpty(c.TaskId) && !c.IsFailed).ToList();
            while (pendingChunks.Any())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var elapsed = DateTime.UtcNow - startTime;
                if (elapsed > _timeout)
                    throw new TimeoutException($"Split processing timed out after {_timeout.TotalMinutes:F0} minutes");

                await Task.Delay(_pollInterval, cancellationToken);

                foreach (var chunk in pendingChunks.ToList())
                {
                    var status = await CheckTaskStatusAsync(chunk.TaskId, cancellationToken);

                        if (status == "SUCCESS")
                        {
                            chunk.IsComplete = true;
                            chunk.Result = await GetResultAsync(chunk.TaskId, cancellationToken);
                            pendingChunks.Remove(chunk);
                            completedChunks++;
                            
                            Report(completedChunks, numChunks, waveNumber, totalWaves, 
                                $"Wave {waveNumber}/{totalWaves}: {completedChunks}/{numChunks} chunks done");
                            
                            // Fire chunk completion callback for pipelined processing
                            if (OnChunkComplete != null && !string.IsNullOrEmpty(chunk.Result))
                            {
                                try
                                {
                                    OnChunkComplete(chunk.Index, chunk.StartPage, chunk.EndPage, chunk.Result);
                                }
                                catch
                                {
                                    // Don't let callback errors break conversion
                                }
                            }
                        }
                    else if (status == "FAILURE" || status == "REVOKED")
                    {
                        chunk.IsFailed = true;
                        pendingChunks.Remove(chunk);
                        completedChunks++; // Count failures too for progress
                        
                        Report(completedChunks, numChunks, waveNumber, totalWaves,
                            $"Wave {waveNumber}/{totalWaves}: chunk failed");
                    }
                }
            }
        }

        var totalElapsed = DateTime.UtcNow - startTime;
        var successCount = allChunks.Count(c => c.IsComplete);
        Report(numChunks, numChunks, totalWaves, totalWaves, 
            $"Converted {successCount}/{numChunks} chunks in {totalElapsed.TotalSeconds:F0}s");

        var orderedChunks = allChunks
            .Where(c => c.IsComplete && !string.IsNullOrEmpty(c.Result))
            .OrderBy(c => c.StartPage)
            .ToList();

        if (orderedChunks.Count == 0) 
            throw new Exception("No chunks were successfully converted");

        // Inject page markers into markdown so chunker can extract page numbers
        var combinedMarkdown = string.Join("\n\n---\n\n", orderedChunks.Select(c => 
            $"<!-- PAGE:{c.StartPage}-{c.EndPage} -->\n{c.Result}"));

        if (IsGarbageText(combinedMarkdown))
        {
            Report(numChunks, numChunks, totalWaves, totalWaves, "Trying PdfPig fallback...");
            try
            {
                var pdfPigResult = ExtractWithPdfPig(filePath);
                if (!string.IsNullOrWhiteSpace(pdfPigResult) && !IsGarbageText(pdfPigResult))
                    return pdfPigResult;
            }
            catch { }
        }

        return combinedMarkdown;
    }

    private async Task<string> ConvertDocxWithSplitProcessingAsync(string filePath, CancellationToken cancellationToken)
    {
        List<DocxChapter> chapters;
        try
        {
            chapters = GetDocxChapters(filePath);
            Report(0, 1, 0, 1, $"DOCX: {chapters.Count} chapters/sections");
        }
        catch (Exception ex)
        {
            Report(0, 1, 0, 1, $"Could not read DOCX: {ex.Message}");
            return await ConvertStandardAsync(filePath, cancellationToken);
        }

        if (chapters.Count <= 3)
        {
            Report(0, 1, 0, 1, "Small DOCX - standard conversion");
            return await ConvertStandardAsync(filePath, cancellationToken);
        }

        var tempDir = Path.Combine(Path.GetTempPath(), $"docsummarizer_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var maxConcurrent = _config.MaxConcurrentChunks;
            var totalWaves = (int)Math.Ceiling((double)chapters.Count / maxConcurrent);
            
            Report(0, chapters.Count, 0, totalWaves, $"Processing {chapters.Count} chapters");

            var chunkTasks = new List<DocxChunkTask>();
            var startTime = DateTime.UtcNow;

            for (var i = 0; i < chapters.Count; i++)
            {
                var chapter = chapters[i];
                var tempPath = Path.Combine(tempDir, $"chapter_{i:D3}.docx");
                CreateDocxFromChapter(filePath, chapter, tempPath);
                chunkTasks.Add(new DocxChunkTask(i, chapter.Title, tempPath));
            }

            var waveNumber = 0;
            var completedChunks = 0;

            for (var waveStart = 0; waveStart < chunkTasks.Count; waveStart += maxConcurrent)
            {
                cancellationToken.ThrowIfCancellationRequested();
                waveNumber++;

                var waveChunks = chunkTasks.Skip(waveStart).Take(maxConcurrent).ToList();
                var chapterNames = string.Join(", ", waveChunks.Select(c => 
                    c.Title.Length > 15 ? c.Title[..15] + "..." : c.Title));
                
                Report(completedChunks, chapters.Count, waveNumber, totalWaves, 
                    $"Wave {waveNumber}/{totalWaves}: {chapterNames}");

                foreach (var chunk in waveChunks)
                {
                    try
                    {
                        chunk.TaskId = await StartConversionAsync(chunk.TempPath, null, null, null, cancellationToken);
                    }
                    catch
                    {
                        chunk.IsFailed = true;
                    }
                }

                var pendingChunks = waveChunks.Where(c => !string.IsNullOrEmpty(c.TaskId) && !c.IsFailed).ToList();
                while (pendingChunks.Any())
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var elapsed = DateTime.UtcNow - startTime;
                    if (elapsed > _timeout)
                        throw new TimeoutException($"Split processing timed out after {_timeout.TotalMinutes:F0} minutes");

                    await Task.Delay(_pollInterval, cancellationToken);

                    foreach (var chunk in pendingChunks.ToList())
                    {
                        var status = await CheckTaskStatusAsync(chunk.TaskId, cancellationToken);

                        if (status == "SUCCESS")
                        {
                            chunk.IsComplete = true;
                            chunk.Result = await GetResultAsync(chunk.TaskId, cancellationToken);
                            pendingChunks.Remove(chunk);
                            completedChunks++;
                            
                            Report(completedChunks, chapters.Count, waveNumber, totalWaves,
                                $"Wave {waveNumber}/{totalWaves}: {completedChunks}/{chapters.Count} chapters done");
                            
                            // Fire chunk completion callback for pipelined processing (DOCX)
                            if (OnChunkComplete != null && !string.IsNullOrEmpty(chunk.Result))
                            {
                                try
                                {
                                    OnChunkComplete(chunk.Index, chunk.Index, chunk.Index, chunk.Result);
                                }
                                catch
                                {
                                    // Don't let callback errors break conversion
                                }
                            }
                        }
                        else if (status == "FAILURE" || status == "REVOKED")
                        {
                            chunk.IsFailed = true;
                            pendingChunks.Remove(chunk);
                            completedChunks++;
                            
                            Report(completedChunks, chapters.Count, waveNumber, totalWaves,
                                $"Wave {waveNumber}/{totalWaves}: chapter failed");
                        }
                    }
                }
            }

            var totalElapsed = DateTime.UtcNow - startTime;
            var successCount = chunkTasks.Count(c => c.IsComplete);
            Report(chapters.Count, chapters.Count, totalWaves, totalWaves,
                $"Converted {successCount}/{chapters.Count} chapters in {totalElapsed.TotalSeconds:F0}s");

            var orderedChunks = chunkTasks
                .Where(c => c.IsComplete && !string.IsNullOrEmpty(c.Result))
                .OrderBy(c => c.Index)
                .ToList();

            if (orderedChunks.Count == 0) 
                throw new Exception("No chapters were successfully converted");

            var sb = new StringBuilder();
            foreach (var chunk in orderedChunks)
            {
                if (sb.Length > 0) sb.AppendLine("\n---\n");
                sb.AppendLine(chunk.Result);
            }

            return sb.ToString();
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    private async Task<string> WaitForCompletionAsync(string taskId, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_timeout);

        var startTime = DateTime.UtcNow;

        while (!timeoutCts.Token.IsCancellationRequested)
        {
            try
            {
                var elapsed = DateTime.UtcNow - startTime;
                var statusResponse = await _http.GetAsync($"{_baseUrl}/v1/status/poll/{taskId}", timeoutCts.Token);

                if (!statusResponse.IsSuccessStatusCode)
                {
                    await Task.Delay(_pollInterval, timeoutCts.Token);
                    continue;
                }

                var statusJson = await statusResponse.Content.ReadAsStringAsync(timeoutCts.Token);
                var status = JsonSerializer.Deserialize(statusJson, DocSummarizerJsonContext.Default.DoclingStatusResponse);
                var taskStatus = status?.TaskStatus?.ToUpperInvariant();

                if (taskStatus == "SUCCESS")
                    return await GetResultAsync(taskId, timeoutCts.Token);

                if (taskStatus == "FAILURE" || taskStatus == "REVOKED")
                    throw new Exception($"Docling conversion failed: {status?.TaskStatus}");

                Report(0, 1, 1, 1, $"Converting... {taskStatus} ({elapsed.TotalSeconds:F0}s)");
                await Task.Delay(_pollInterval, timeoutCts.Token);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"Document conversion timed out after {_timeout.TotalMinutes:F0} minutes");
            }
        }

        throw new TimeoutException($"Document conversion timed out after {_timeout.TotalMinutes:F0} minutes");
    }

    private static int GetPdfPageCount(string filePath)
    {
        using var document = PdfDocument.Open(filePath);
        return document.NumberOfPages;
    }

    private static bool IsGarbageText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        var sample = text.Length > 2000 ? text[..2000] : text;
        var alphaCount = sample.Count(char.IsLetter);
        var upperCount = sample.Count(char.IsUpper);

        if (alphaCount == 0) return false;

        var upperRatio = (double)upperCount / alphaCount;
        if (upperRatio > 0.4 && alphaCount > 50) return true;

        var letterFreq = sample
            .Where(char.IsLetter)
            .GroupBy(char.ToLower)
            .ToDictionary(g => g.Key, g => g.Count());

        if (letterFreq.Count > 0)
        {
            var avgFreq = (double)alphaCount / letterFreq.Count;
            var variance = letterFreq.Values.Average(v => Math.Pow(v - avgFreq, 2));
            var stdDev = Math.Sqrt(variance);
            if (avgFreq > 5 && stdDev / avgFreq > 2.5) return true;
        }

        var vowelCount = sample.Count(c => "aeiouAEIOU".Contains(c));
        var vowelRatio = (double)vowelCount / alphaCount;
        if (vowelRatio < 0.15 && alphaCount > 50) return true;

        return false;
    }

    private static string ExtractWithPdfPig(string filePath)
    {
        var sb = new StringBuilder();
        using var document = PdfDocument.Open(filePath);

        foreach (var page in document.GetPages())
        {
            var text = page.Text;
            if (!string.IsNullOrWhiteSpace(text))
            {
                if (sb.Length > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("---");
                    sb.AppendLine();
                }
                sb.AppendLine(text);
            }
        }

        return sb.ToString();
    }

    private static List<DocxChapter> GetDocxChapters(string filePath)
    {
        var chapters = new List<DocxChapter>();
        using var doc = WordprocessingDocument.Open(filePath, false);
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body == null) return chapters;

        var elements = body.Elements().ToList();
        var currentChapterStart = 0;
        string? currentTitle = null;

        for (var i = 0; i < elements.Count; i++)
        {
            if (elements[i] is Paragraph para)
            {
                var styleId = para.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
                if (styleId != null && (styleId.StartsWith("Heading1", StringComparison.OrdinalIgnoreCase) ||
                                        styleId.Equals("Title", StringComparison.OrdinalIgnoreCase) ||
                                        styleId.StartsWith("Heading2", StringComparison.OrdinalIgnoreCase)))
                {
                    if (currentTitle != null && i > currentChapterStart)
                        chapters.Add(new DocxChapter(currentTitle, currentChapterStart, i - 1));

                    currentTitle = GetParagraphText(para);
                    if (string.IsNullOrWhiteSpace(currentTitle))
                        currentTitle = $"Section {chapters.Count + 1}";
                    currentChapterStart = i;
                }
            }
        }

        if (currentTitle != null)
            chapters.Add(new DocxChapter(currentTitle, currentChapterStart, elements.Count - 1));
        else if (elements.Count > 0)
            chapters.Add(new DocxChapter("Document", 0, elements.Count - 1));

        return chapters;
    }

    private static string GetParagraphText(Paragraph para)
    {
        var sb = new StringBuilder();
        foreach (var run in para.Elements<Run>())
            foreach (var text in run.Elements<Text>())
                sb.Append(text.Text);
        return sb.ToString().Trim();
    }

    private static void CreateDocxFromChapter(string sourcePath, DocxChapter chapter, string destPath)
    {
        File.Copy(sourcePath, destPath, true);
        using var doc = WordprocessingDocument.Open(destPath, true);
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body == null) return;

        var elements = body.Elements().ToList();
        for (var i = elements.Count - 1; i >= 0; i--)
            if (i < chapter.StartIndex || i > chapter.EndIndex)
                elements[i].Remove();

        doc.MainDocumentPart?.Document?.Save();
    }

    private async Task<string?> CheckTaskStatusAsync(string taskId, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _http.GetAsync($"{_baseUrl}/v1/status/poll/{taskId}", cancellationToken);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var status = JsonSerializer.Deserialize(json, DocSummarizerJsonContext.Default.DoclingStatusResponse);
            return status?.TaskStatus?.ToUpperInvariant();
        }
        catch { return null; }
    }

    private async Task<string> StartConversionAsync(string filePath, int? startPage, int? endPage, string? backend, CancellationToken cancellationToken)
    {
        using var content = new MultipartFormDataContent();
        await using var stream = File.OpenRead(filePath);
        var streamContent = new StreamContent(stream);
        content.Add(streamContent, "files", Path.GetFileName(filePath));

        var chosenBackend = backend ?? _config.PdfBackend;
        if (filePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(chosenBackend))
            content.Add(new StringContent(chosenBackend), "pdf_backend");

        if (startPage.HasValue && endPage.HasValue)
        {
            content.Add(new StringContent(startPage.Value.ToString()), "page_range");
            content.Add(new StringContent(endPage.Value.ToString()), "page_range");
        }

        var response = await _http.PostAsync($"{_baseUrl}/v1/convert/file/async", content, cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var taskResponse = JsonSerializer.Deserialize(json, DocSummarizerJsonContext.Default.DoclingTaskResponse);

        return taskResponse?.TaskId ?? throw new Exception("No task ID returned from Docling");
    }

    private async Task<string> GetResultAsync(string taskId, CancellationToken cancellationToken)
    {
        var response = await _http.GetAsync($"{_baseUrl}/v1/result/{taskId}", cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var result = JsonSerializer.Deserialize(json, DocSummarizerJsonContext.Default.DoclingResultResponse);

        return result?.Document?.MdContent ?? throw new Exception("No markdown content returned from Docling");
    }

    public async Task<bool> IsAvailableAsync()
    {
        try
        {
            var response = await _http.GetAsync($"{_baseUrl}/health");
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }
    
    /// <summary>
    /// Detect if Docling is running with GPU acceleration and adapt config accordingly.
    /// Call this before processing to optimize settings.
    /// </summary>
    public async Task<DoclingCapabilities> DetectCapabilitiesAsync()
    {
        var capabilities = new DoclingCapabilities();
        
        try
        {
            // Try /health endpoint first (standard)
            var response = await _http.GetAsync($"{_baseUrl}/health");
            if (!response.IsSuccessStatusCode)
            {
                capabilities.Available = false;
                return capabilities;
            }
            
            capabilities.Available = true;
            
            // Try to get more detailed info - Docling serve may have /info or similar
            // Check response body for GPU indicators
            var healthContent = await response.Content.ReadAsStringAsync();
            
            // Look for GPU/CUDA indicators in health response
            var lowerContent = healthContent.ToLowerInvariant();
            if (lowerContent.Contains("cuda") || lowerContent.Contains("gpu") || lowerContent.Contains("nvidia"))
            {
                capabilities.HasGpu = true;
            }
            
            // Try /v1/info or /info endpoint if available
            try
            {
                var infoResponse = await _http.GetAsync($"{_baseUrl}/v1/info");
                if (infoResponse.IsSuccessStatusCode)
                {
                    var infoContent = await infoResponse.Content.ReadAsStringAsync();
                    var lowerInfo = infoContent.ToLowerInvariant();
                    
                    if (lowerInfo.Contains("cuda") || lowerInfo.Contains("gpu") || lowerInfo.Contains("nvidia"))
                    {
                        capabilities.HasGpu = true;
                    }
                    
                    // Try to parse accelerator info
                    if (lowerInfo.Contains("\"accelerator\""))
                    {
                        capabilities.HasGpu = lowerInfo.Contains("\"cuda\"") || lowerInfo.Contains("\"gpu\"");
                    }
                }
            }
            catch
            {
                // Info endpoint not available, that's fine
            }
            
            // If we still don't know, try a timing-based heuristic
            // GPU conversion is typically 5-10x faster
            if (!capabilities.HasGpu.HasValue && _config.AutoDetectGpu)
            {
                capabilities.HasGpu = await DetectGpuByTimingAsync();
            }
            
            _hasGpu = capabilities.HasGpu;
        }
        catch
        {
            capabilities.Available = false;
        }
        
        return capabilities;
    }
    
    /// <summary>
    /// Detect GPU by timing a small conversion (fallback method)
    /// </summary>
    private Task<bool?> DetectGpuByTimingAsync()
    {
        // This is a rough heuristic - don't use unless needed
        // A GPU typically processes pages in <1 second each, CPU takes 3-10 seconds
        return Task.FromResult<bool?>(null); // For now, don't do timing detection - too invasive
    }
    
    /// <summary>
    /// Get optimal config settings based on detected capabilities
    /// </summary>
    public DoclingConfig GetOptimizedConfig(DoclingCapabilities? capabilities = null)
    {
        var hasGpu = capabilities?.HasGpu ?? _hasGpu ?? false;
        
        // Create a copy of current config with optimized settings
        var optimized = new DoclingConfig
        {
            BaseUrl = _config.BaseUrl,
            TimeoutSeconds = _config.TimeoutSeconds,
            PdfBackend = _config.PdfBackend,
            AutoDetectGpu = _config.AutoDetectGpu
        };
        
        if (hasGpu)
        {
            // GPU-optimized settings:
            // - Larger chunks (GPU handles them efficiently)
            // - Single concurrent (GPU parallelism is internal)
            // - Higher threshold before splitting (GPU can handle more pages at once)
            optimized.EnableSplitProcessing = _config.EnableSplitProcessing;
            optimized.PagesPerChunk = Math.Max(_config.PagesPerChunk, 100);
            optimized.MaxConcurrentChunks = 1;
            optimized.MinPagesForSplit = Math.Max(_config.MinPagesForSplit, 150);
        }
        else
        {
            // CPU-optimized settings:
            // - Smaller chunks (lower memory usage)
            // - Multiple concurrent (use CPU cores)
            // - Lower threshold for splitting (avoid memory issues)
            optimized.EnableSplitProcessing = true;
            optimized.PagesPerChunk = Math.Min(_config.PagesPerChunk, 30);
            optimized.MaxConcurrentChunks = Math.Max(_config.MaxConcurrentChunks, 2);
            optimized.MinPagesForSplit = Math.Min(_config.MinPagesForSplit, 40);
        }
        
        return optimized;
    }

    private record DocxChapter(string Title, int StartIndex, int EndIndex);

    private class DocxChunkTask
    {
        public DocxChunkTask(int index, string title, string tempPath)
        {
            Index = index;
            Title = title;
            TempPath = tempPath;
        }

        public int Index { get; }
        public string Title { get; }
        public string TempPath { get; }
        public string TaskId { get; set; } = "";
        public bool IsComplete { get; set; }
        public bool IsFailed { get; set; }
        public string? Result { get; set; }
    }

    private class PdfChunkTask
    {
        public PdfChunkTask(int index, int startPage, int endPage, string taskId)
        {
            Index = index;
            StartPage = startPage;
            EndPage = endPage;
            TaskId = taskId;
        }

        public int Index { get; }
        public int StartPage { get; }
        public int EndPage { get; }
        public string TaskId { get; set; }
        public bool IsComplete { get; set; }
        public bool IsFailed { get; set; }
        public string? Result { get; set; }
    }
}

public class DoclingTaskResponse
{
    [JsonPropertyName("task_id")] public string? TaskId { get; set; }
}

public class DoclingStatusResponse
{
    [JsonPropertyName("task_id")] public string? TaskId { get; set; }
    [JsonPropertyName("task_status")] public string? TaskStatus { get; set; }
}

public class DoclingResultResponse
{
    [JsonPropertyName("document")] public DoclingDocument? Document { get; set; }
}

public class DoclingDocument
{
    [JsonPropertyName("md_content")] public string? MdContent { get; set; }
}

public class DoclingResponse
{
    [JsonPropertyName("document")] public DoclingDocument? Document { get; set; }
}
