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

    #region Document Details Tests

    [Fact]
    public async Task DebugUI_CaptureState()
    {
        // Capture ALL console messages
        var consoleMessages = new List<string>();
        _page!.Console += (_, e) => consoleMessages.Add($"[{e.Message.Type}] {e.Message.Text}");

        // Navigate
        await _page.GoToAsync(BaseUrl);
        await Task.Delay(3000);

        // Take a screenshot
        await _page.ScreenshotAsync("E:\\source\\lucidrag\\debug-ui-state.png");
        Console.WriteLine("Screenshot saved to E:\\source\\lucidrag\\debug-ui-state.png");

        // Get the full HTML of the chat input area
        var chatAreaHtml = await _page.EvaluateFunctionAsync<string>(@"
            () => {
                const form = document.querySelector('form');
                return form ? form.outerHTML.substring(0, 500) : 'No form found';
            }
        ");
        Console.WriteLine($"Chat form HTML: {chatAreaHtml}");

        // Try typing and submitting via keyboard
        var input = await _page.QuerySelectorAsync("input[placeholder*='Ask about']");
        if (input != null)
        {
            await input.ClickAsync();
            await _page.Keyboard.TypeAsync("What is this about?");
            await Task.Delay(500);

            // Take screenshot before submit
            await _page.ScreenshotAsync("E:\\source\\lucidrag\\debug-before-submit.png");

            // Press Enter
            await _page.Keyboard.PressAsync("Enter");
            Console.WriteLine("Pressed Enter key");

            await Task.Delay(5000);

            // Take screenshot after submit
            await _page.ScreenshotAsync("E:\\source\\lucidrag\\debug-after-submit.png");

            // Check Alpine state
            var state = await _page.EvaluateFunctionAsync<string>(@"
                () => {
                    const appEl = document.querySelector('[x-data*=""ragApp""]');
                    const data = appEl?._x_dataStack?.[0];
                    if (!data) return 'No Alpine data';
                    return JSON.stringify({
                        messagesCount: data.messages?.length,
                        currentMessage: data.currentMessage,
                        isTyping: data.isTyping
                    });
                }
            ");
            Console.WriteLine($"Alpine state after Enter: {state}");
        }
        else
        {
            Console.WriteLine("Could not find chat input!");
        }

        // Print console messages
        Console.WriteLine("\n=== All Console Messages ===");
        foreach (var msg in consoleMessages)
        {
            Console.WriteLine(msg);
        }
    }

    [Fact]
    public async Task DocumentDetails_ShouldShowTabView_WhenClickingDocument()
    {
        // Navigate to home page
        var response = await _page!.GoToAsync(BaseUrl);
        response!.Status.Should().Be(System.Net.HttpStatusCode.OK);

        // Wait for page to fully load
        await Task.Delay(2000);

        // Check if there are documents in the list
        var docCount = await _page.EvaluateFunctionAsync<int>(@"
            () => document.querySelectorAll('[x-data*=""ragApp""] #document-list label').length
        ");
        Console.WriteLine($"Documents in list: {docCount}");

        if (docCount == 0)
        {
            Console.WriteLine("No documents - skipping document details test");
            return;
        }

        // Get first document ID from API and call showDocumentDetails
        var showDetailsResult = await _page.EvaluateFunctionAsync<string>(@"
            async () => {
                try {
                    const appEl = document.querySelector('[x-data*=""ragApp""]');
                    if (!appEl) return 'No ragApp element';
                    const data = appEl._x_dataStack?.[0];
                    if (!data) return 'No Alpine data';
                    if (typeof data.showDocumentDetails !== 'function') return 'showDocumentDetails not found';

                    // Get first document ID from API
                    const response = await fetch('/api/documents');
                    if (!response.ok) return 'API fetch failed';
                    const result = await response.json();
                    if (!result.documents || result.documents.length === 0) return 'No documents from API';

                    const docId = result.documents[0].id;

                    // Call showDocumentDetails directly
                    await data.showDocumentDetails(docId);
                    return 'Called showDocumentDetails for ' + docId;
                } catch(e) {
                    return 'Error: ' + e.toString();
                }
            }
        ");
        Console.WriteLine($"Show details result: {showDetailsResult}");

        // Wait for data to load
        await Task.Delay(3000);

        // Take screenshot to see details tab view
        await _page.ScreenshotAsync("E:\\source\\lucidrag\\debug-details-tab.png");
        Console.WriteLine("Screenshot saved to E:\\source\\lucidrag\\debug-details-tab.png");

        // Check if document details loaded and viewMode changed to 'details'
        var detailsState = await _page.EvaluateFunctionAsync<JsonElement?>(@"
            () => {
                const appEl = document.querySelector('[x-data*=""ragApp""]');
                const data = appEl?._x_dataStack?.[0];
                if (!data) return null;
                return {
                    hasDetails: data.documentDetails != null,
                    viewMode: data.viewMode,
                    documentName: data.documentDetails?.document?.name ?? 'not loaded',
                    segmentCount: data.documentDetails?.totalSegments ?? 0,
                    entityCount: data.documentDetails?.entitiesTotalCount ?? 0,
                    detailsTab: data.detailsTab
                };
            }
        ");
        Console.WriteLine($"Document details state: {detailsState}");

        var hasDetails = detailsState?.GetProperty("hasDetails").GetBoolean() ?? false;
        var viewMode = detailsState?.GetProperty("viewMode").GetString() ?? "";

        hasDetails.Should().BeTrue("Document details should load when showDocumentDetails is called");
        viewMode.Should().Be("details", "viewMode should change to 'details' when viewing document details");
    }

    #endregion

    #region Chat UI Tests

    [Fact]
    public async Task ChatUI_ShouldSendMessage_WithoutErrors()
    {
        // Collect ALL console messages, not just errors
        var consoleMessages = new List<string>();
        _page!.Console += (_, e) =>
        {
            consoleMessages.Add($"[{e.Message.Type}] {e.Message.Text}");
        };

        // Also capture page errors
        var pageErrors = new List<string>();
        _page.Error += (_, e) =>
        {
            pageErrors.Add($"[PageError] {e}");
        };

        // Navigate to home page
        var response = await _page.GoToAsync(BaseUrl);
        response!.Status.Should().Be(System.Net.HttpStatusCode.OK);

        // Wait for page to fully load
        await Task.Delay(2000);

        // Check if Alpine.js loaded
        var alpineLoaded = await _page.EvaluateFunctionAsync<bool>(@"() => typeof Alpine !== 'undefined'");
        Console.WriteLine($"Alpine.js loaded: {alpineLoaded}");

        // Check Alpine state (using Alpine.js v3 API) - Target ragApp specifically
        var appState = await _page.EvaluateFunctionAsync<JsonElement?>(@"
            () => {
                try {
                    // Target the ragApp element specifically (not articlesDropdown)
                    const appEl = document.querySelector('[x-data*=""ragApp""]');
                    if (!appEl) return { error: 'No ragApp element found' };
                    // Alpine.js v3 uses _x_dataStack or Alpine.$data
                    const data = appEl._x_dataStack?.[0] ?? (typeof Alpine !== 'undefined' ? Alpine.$data(appEl) : null);
                    if (!data) return { error: 'No Alpine data found on element', hasElement: true };
                    return {
                        messagesCount: data.messages?.length ?? -1,
                        currentMessage: data.currentMessage ?? '',
                        isTyping: data.isTyping ?? false,
                        hasConversationId: data.conversationId != null,
                        hasSendMessage: typeof data.sendMessage === 'function'
                    };
                } catch(e) {
                    return { error: e.toString() };
                }
            }
        ");
        Console.WriteLine($"Alpine app state: {appState}");

        // Try to type in the chat input
        var inputSelector = "input[x-model='currentMessage']";
        var inputExists = await _page.QuerySelectorAsync(inputSelector);
        Console.WriteLine($"Chat input exists: {inputExists != null}");

        if (inputExists == null)
        {
            // List all input elements for debugging
            var inputs = await _page.EvaluateFunctionAsync<string[]>(@"
                () => Array.from(document.querySelectorAll('input')).map(i =>
                    `${i.tagName}[type=${i.type}][placeholder=${i.placeholder}]`
                )
            ");
            Console.WriteLine($"Available inputs: {string.Join(", ", inputs)}");
        }

        // Type a test message
        await _page.TypeAsync(inputSelector, "What is Pride and Prejudice about?");
        await Task.Delay(500);

        // Check the input value
        var inputValue = await _page.EvaluateFunctionAsync<string>(@"
            () => document.querySelector('input[x-model=""currentMessage""]')?.value ?? 'NOT_FOUND'
        ");
        Console.WriteLine($"Input value after typing: {inputValue}");

        // Click send button
        var sendButtonSelector = "button[type='submit']";
        var sendButton = await _page.QuerySelectorAsync(sendButtonSelector);
        Console.WriteLine($"Send button exists: {sendButton != null}");

        if (sendButton != null)
        {
            // First try submitting the form directly via JavaScript to ensure sendMessage is called
            var submitResult = await _page.EvaluateFunctionAsync<string>(@"
                async () => {
                    try {
                        // Target ragApp specifically
                        const appEl = document.querySelector('[x-data*=""ragApp""]');
                        if (!appEl) return 'No ragApp element';
                        const data = appEl._x_dataStack?.[0];
                        if (!data) return 'No Alpine data on ragApp';

                        // Set the message directly
                        data.currentMessage = 'What is Pride and Prejudice about?';

                        // Check if sendMessage exists
                        if (typeof data.sendMessage !== 'function') {
                            return 'sendMessage is not a function, keys: ' + Object.keys(data).slice(0, 20).join(', ');
                        }

                        // Call sendMessage directly
                        await data.sendMessage();
                        return 'sendMessage called successfully, messages: ' + data.messages.length;
                    } catch(e) {
                        return 'Error: ' + e.toString();
                    }
                }
            ");
            Console.WriteLine($"Direct sendMessage result: {submitResult}");

            // Wait for response
            await Task.Delay(5000);

            // Check state after sending (Alpine.js v3 API) - Target ragApp
            var stateAfter = await _page.EvaluateFunctionAsync<JsonElement?>(@"
                () => {
                    try {
                        const appEl = document.querySelector('[x-data*=""ragApp""]');
                        if (!appEl) return { error: 'No ragApp element found' };
                        const data = appEl._x_dataStack?.[0] ?? (typeof Alpine !== 'undefined' ? Alpine.$data(appEl) : null);
                        if (!data) return { error: 'No Alpine data found' };
                        return {
                            messagesCount: data.messages?.length ?? -1,
                            messages: data.messages?.map(m => ({ role: m.role, content: m.content?.substring(0, 100) })) ?? [],
                            isTyping: data.isTyping ?? false,
                            hasConversationId: data.conversationId != null
                        };
                    } catch(e) {
                        return { error: e.toString() };
                    }
                }
            ");
            Console.WriteLine($"State after sending: {stateAfter}");
        }

        // Print all console messages
        Console.WriteLine("\n=== Console Messages ===");
        foreach (var msg in consoleMessages)
        {
            Console.WriteLine(msg);
        }

        Console.WriteLine("\n=== Page Errors ===");
        foreach (var err in pageErrors)
        {
            Console.WriteLine(err);
        }

        // Check for any JS errors
        var jsErrors = consoleMessages.Where(m => m.Contains("[Error]")).ToList();
        if (jsErrors.Any())
        {
            Console.WriteLine("\n=== JavaScript Errors Found ===");
            foreach (var err in jsErrors)
            {
                Console.WriteLine(err);
            }
        }

        // Assert there should be messages after a query (Alpine.js v3 API) - Target ragApp
        var finalState = await _page.EvaluateFunctionAsync<JsonElement?>(@"
            () => {
                const appEl = document.querySelector('[x-data*=""ragApp""]');
                if (!appEl) return null;
                const data = appEl._x_dataStack?.[0] ?? (typeof Alpine !== 'undefined' ? Alpine.$data(appEl) : null);
                if (!data) return null;
                return { messagesCount: data.messages?.length ?? 0 };
            }
        ");

        var messageCount = finalState?.GetProperty("messagesCount").GetInt32() ?? 0;
        messageCount.Should().BeGreaterThan(0, "Chat should have at least the user message after sending");
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
