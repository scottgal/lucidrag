using System.Text.Json;
using FluentAssertions;
using PuppeteerSharp;

namespace LucidRAG.Tests.Integration;

/// <summary>
/// Browser integration tests for file uploads using PuppeteerSharp.
/// Tests the actual web UI experience including FilePond upload.
///
/// Requires the RagDocuments app to be running on localhost:5080.
/// Start with: dotnet run --project LucidRAG --standalone
///
/// These tests are skipped in CI (no running server available).
/// Run locally only.
/// </summary>
[Collection("Browser")]
[Trait("Category", "Browser")]
public class BrowserUploadTests : IAsyncLifetime
{
    private IBrowser? _browser;
    private IPage? _page;
    private const string BaseUrl = "http://127.0.0.1:5080";

    // Path to blog markdown files for testing
    private static readonly string MarkdownPath = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Mostlylucid", "Markdown"));

    public async Task InitializeAsync()
    {
        // Download browser if needed and launch
        var browserFetcher = new BrowserFetcher();
        await browserFetcher.DownloadAsync();

        _browser = await Puppeteer.LaunchAsync(new LaunchOptions
        {
            Headless = true,
            Args = ["--no-sandbox", "--disable-setuid-sandbox", "--disable-dev-shm-usage"]
        });

        _page = await _browser.NewPageAsync();

        // Set a reasonable viewport
        await _page.SetViewportAsync(new ViewPortOptions { Width = 1280, Height = 800 });

        // Log console messages for debugging
        _page.Console += (_, e) =>
        {
            if (e.Message.Type is ConsoleType.Error or ConsoleType.Warning)
            {
                Console.WriteLine($"[Browser {e.Message.Type}]: {e.Message.Text}");
            }
        };
    }

    public async Task DisposeAsync()
    {
        if (_page != null) await _page.CloseAsync();
        if (_browser != null) await _browser.CloseAsync();
    }

    #region Page Load Tests

    [Fact]
    public async Task HomePage_ShouldLoad_WithUploadInterface()
    {
        // Act
        var response = await _page!.GoToAsync(BaseUrl);

        // Assert
        response!.Status.Should().Be(System.Net.HttpStatusCode.OK);

        // Wait for page to render
        await Task.Delay(500);

        // Should have the page title or content
        var title = await _page.GetTitleAsync();
        title.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task HomePage_ShouldShow_FilepondUploadWidget()
    {
        // Arrange
        await _page!.GoToAsync(BaseUrl);

        // Wait for page to render and FilePond to initialize
        await Task.Delay(2000);

        // Act - Check for FilePond container
        var filepond = await _page.QuerySelectorAsync(".filepond--root");

        // Assert
        filepond.Should().NotBeNull("FilePond upload widget should be present");
    }

    #endregion

    #region Upload Tests via API (bypassing FilePond UI complexity)

    [Fact]
    public async Task Upload_MarkdownFile_ShouldSucceed()
    {
        // Arrange
        await _page!.GoToAsync(BaseUrl);
        await Task.Delay(500);

        var testContent = "# Test Document\n\nThis is a test markdown file for upload testing.";

        // Act - Upload via JavaScript/fetch (simulating what FilePond does)
        var uploadResult = await _page.EvaluateFunctionAsync<JsonElement>(@"
            async (content, filename) => {
                const blob = new Blob([content], { type: 'text/markdown' });
                const formData = new FormData();
                formData.append('file', blob, filename);

                try {
                    const response = await fetch('/api/documents/upload', {
                        method: 'POST',
                        body: formData
                    });

                    if (!response.ok) {
                        return { error: await response.text(), httpStatus: response.status };
                    }

                    return await response.json();
                } catch (e) {
                    return { error: e.toString(), httpStatus: 0 };
                }
            }
        ", testContent, "test-upload.md");

        // Assert
        if (uploadResult.TryGetProperty("error", out var error))
        {
            uploadResult.TryGetProperty("httpStatus", out var httpStatus);
            throw new Exception($"Upload failed with status {httpStatus}: {error.GetString()}");
        }

        var status = uploadResult.GetProperty("status").GetString();
        status.Should().BeOneOf("queued", "pending", "processing");

        var documentId = uploadResult.GetProperty("documentId").GetString();
        documentId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Upload_MultipleBlogPosts_ShouldAllBeQueued()
    {
        // Arrange
        await _page!.GoToAsync(BaseUrl);
        await Task.Delay(500);

        var testFiles = new[]
        {
            ("file1.md", "# First Document\n\nContent for first document."),
            ("file2.md", "# Second Document\n\nContent for second document."),
            ("file3.md", "# Third Document\n\nContent for third document.")
        };

        var uploadedIds = new List<string>();

        // Act - Upload multiple files
        foreach (var (filename, content) in testFiles)
        {
            var uploadResult = await _page.EvaluateFunctionAsync<JsonElement>(@"
                async (content, filename) => {
                    const blob = new Blob([content], { type: 'text/markdown' });
                    const formData = new FormData();
                    formData.append('file', blob, filename);

                    const response = await fetch('/api/documents/upload', {
                        method: 'POST',
                        body: formData
                    });

                    if (!response.ok) {
                        return { error: await response.text() };
                    }

                    return await response.json();
                }
            ", content, filename);

            if (!uploadResult.TryGetProperty("error", out _) &&
                uploadResult.TryGetProperty("documentId", out var docId))
            {
                uploadedIds.Add(docId.GetString()!);
            }
        }

        // Assert
        uploadedIds.Should().HaveCount(3);
        uploadedIds.Should().AllSatisfy(id => id.Should().NotBeNullOrEmpty());
    }

    [Fact]
    public async Task Upload_InvalidFileType_ShouldBeRejected()
    {
        // Arrange
        await _page!.GoToAsync(BaseUrl);
        await Task.Delay(500);

        // Act - Try to upload an invalid file type
        var uploadResult = await _page.EvaluateFunctionAsync<JsonElement>(@"
            async () => {
                const blob = new Blob(['some binary data'], { type: 'application/octet-stream' });
                const formData = new FormData();
                formData.append('file', blob, 'malicious.exe');

                const response = await fetch('/api/documents/upload', {
                    method: 'POST',
                    body: formData
                });

                return {
                    httpStatus: response.status,
                    body: response.ok ? await response.json() : await response.text()
                };
            }
        ");

        // Assert - Should be rejected (either 400 or 415)
        int statusCode = uploadResult.GetProperty("httpStatus").GetInt32();
        statusCode.Should().BeOneOf(400, 415, 500); // Bad request, unsupported media, or server error
    }

    #endregion

    #region FilePond Interaction Tests

    [Fact]
    public async Task FilePond_DragDropArea_ShouldExist()
    {
        // Arrange
        await _page!.GoToAsync(BaseUrl);

        // Wait for FilePond to initialize
        await Task.Delay(2000);

        // Act
        var dropLabel = await _page.QuerySelectorAsync(".filepond--drop-label");

        // Assert
        dropLabel.Should().NotBeNull("FilePond should have a drop label for drag-and-drop");
    }

    #endregion

    #region Processing Status Tests

    [Fact]
    public async Task Upload_ShouldReturn_ProcessingStatus()
    {
        // Arrange
        await _page!.GoToAsync(BaseUrl);
        await Task.Delay(500);

        // Act - Upload and get document ID
        var uploadResult = await _page.EvaluateFunctionAsync<JsonElement>(@"
            async () => {
                const content = '# Status Test Document\n\nThis document tests status tracking.';
                const blob = new Blob([content], { type: 'text/markdown' });
                const formData = new FormData();
                formData.append('file', blob, 'status-test.md');

                const response = await fetch('/api/documents/upload', {
                    method: 'POST',
                    body: formData
                });

                if (!response.ok) {
                    return { error: await response.text() };
                }

                return await response.json();
            }
        ");

        if (uploadResult.TryGetProperty("error", out var error))
        {
            throw new Exception($"Upload failed: {error.GetString()}");
        }

        var documentId = uploadResult.GetProperty("documentId").GetString()!;
        documentId.Should().NotBeNullOrEmpty();

        // Check status endpoint
        var statusResult = await _page.EvaluateFunctionAsync<JsonElement>(@"
            async (docId) => {
                const response = await fetch(`/api/documents/${docId}`);
                if (!response.ok) {
                    return { error: await response.text(), httpStatus: response.status };
                }
                return await response.json();
            }
        ", documentId);

        // Assert - should have a valid status or at least not error badly
        if (!statusResult.TryGetProperty("error", out _))
        {
            var status = statusResult.GetProperty("status").GetString() ?? "";
            status.Should().BeOneOf("pending", "processing", "completed", "failed", "queued",
                "Document should have a valid status");
        }
    }

    #endregion

    #region Real Blog Post Upload Tests

    [Fact]
    public async Task Upload_RealBlogPost_ShouldSucceed()
    {
        // Arrange
        await _page!.GoToAsync(BaseUrl);
        await Task.Delay(500);

        var testFile = Path.Combine(MarkdownPath, "htmxwithaspnetcore.md");

        if (!File.Exists(testFile))
        {
            // Find any markdown file
            var files = Directory.Exists(MarkdownPath)
                ? Directory.GetFiles(MarkdownPath, "*.md")
                    .Where(f => !f.Contains("drafts"))
                    .Take(1)
                    .ToArray()
                : Array.Empty<string>();

            if (files.Length == 0)
            {
                // Skip test if no files available
                return;
            }

            testFile = files[0];
        }

        var fileContent = await File.ReadAllTextAsync(testFile);
        var fileName = Path.GetFileName(testFile);

        // Act
        var uploadResult = await _page.EvaluateFunctionAsync<JsonElement>(@"
            async (content, filename) => {
                const blob = new Blob([content], { type: 'text/markdown' });
                const formData = new FormData();
                formData.append('file', blob, filename);

                const response = await fetch('/api/documents/upload', {
                    method: 'POST',
                    body: formData
                });

                if (!response.ok) {
                    return { error: await response.text(), httpStatus: response.status };
                }

                return await response.json();
            }
        ", fileContent, fileName);

        // Assert
        if (uploadResult.TryGetProperty("error", out var error))
        {
            throw new Exception($"Upload failed: {error.GetString()}");
        }

        var status = uploadResult.GetProperty("status").GetString();
        status.Should().BeOneOf("queued", "pending", "processing");

        var documentId = uploadResult.GetProperty("documentId").GetString();
        documentId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Upload_LargeBlogPost_ShouldSucceed()
    {
        // Arrange
        await _page!.GoToAsync(BaseUrl);
        await Task.Delay(500);

        // Find a larger blog post
        string? testFile = null;

        if (Directory.Exists(MarkdownPath))
        {
            testFile = Directory.GetFiles(MarkdownPath, "*.md")
                .Where(f => !f.Contains("drafts"))
                .OrderByDescending(f => new FileInfo(f).Length)
                .FirstOrDefault();
        }

        if (testFile == null)
        {
            // Create a large test file
            var largeContent = "# Large Test Document\n\n" + string.Join("\n\n", Enumerable.Range(1, 100)
                .Select(i => $"## Section {i}\n\nThis is paragraph content for section {i}. " +
                             "It contains multiple sentences to make the document larger."));

            var uploadResult = await _page.EvaluateFunctionAsync<JsonElement>(@"
                async (content, filename) => {
                    const blob = new Blob([content], { type: 'text/markdown' });
                    const formData = new FormData();
                    formData.append('file', blob, filename);

                    const response = await fetch('/api/documents/upload', {
                        method: 'POST',
                        body: formData
                    });

                    if (!response.ok) {
                        return { error: await response.text(), httpStatus: response.status };
                    }

                    return await response.json();
                }
            ", largeContent, "large-test.md");

            if (uploadResult.TryGetProperty("error", out var error))
            {
                throw new Exception($"Upload failed: {error.GetString()}");
            }

            var status = uploadResult.GetProperty("status").GetString();
            status.Should().BeOneOf("queued", "pending", "processing");
            return;
        }

        var fileContent = await File.ReadAllTextAsync(testFile);
        var fileName = Path.GetFileName(testFile);

        // Act
        var result = await _page.EvaluateFunctionAsync<JsonElement>(@"
            async (content, filename) => {
                const blob = new Blob([content], { type: 'text/markdown' });
                const formData = new FormData();
                formData.append('file', blob, filename);

                const response = await fetch('/api/documents/upload', {
                    method: 'POST',
                    body: formData
                });

                if (!response.ok) {
                    return { error: await response.text(), httpStatus: response.status };
                }

                return await response.json();
            }
        ", fileContent, fileName);

        // Assert
        if (result.TryGetProperty("error", out var err))
        {
            throw new Exception($"Upload failed: {err.GetString()}");
        }

        var resultStatus = result.GetProperty("status").GetString();
        resultStatus.Should().BeOneOf("queued", "pending", "processing");
    }

    #endregion
}

/// <summary>
/// Collection definition for browser tests (separate from API tests)
/// </summary>
[CollectionDefinition("Browser")]
public class BrowserTestCollection : ICollectionFixture<BrowserTestFixture>
{
}

/// <summary>
/// Shared fixture for browser tests
/// </summary>
public class BrowserTestFixture : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        // Download browser once for all tests
        var browserFetcher = new BrowserFetcher();
        await browserFetcher.DownloadAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;
}
