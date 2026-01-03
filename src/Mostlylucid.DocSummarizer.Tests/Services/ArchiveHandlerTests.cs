using Xunit;
using Mostlylucid.DocSummarizer.Services;
using System.IO.Compression;
using System.Text;

namespace Mostlylucid.DocSummarizer.Tests.Services;

/// <summary>
/// Unit tests for ArchiveHandler - tests ZIP extraction without external dependencies
/// </summary>
public class ArchiveHandlerTests
{
    private readonly string _tempDir;

    public ArchiveHandlerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ArchiveHandlerTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void InspectArchive_WithNonExistentFile_ReturnsNull()
    {
        // Arrange
        var nonExistentPath = Path.Combine(_tempDir, "nonexistent.zip");

        // Act
        var result = ArchiveHandler.InspectArchive(nonExistentPath);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void InspectArchive_WithNonZipFile_ReturnsNull()
    {
        // Arrange
        var txtPath = Path.Combine(_tempDir, "test.txt");
        File.WriteAllText(txtPath, "Not a zip file");

        // Act
        var result = ArchiveHandler.InspectArchive(txtPath);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void InspectArchive_WithValidZip_ReturnsArchiveInfo()
    {
        // Arrange
        var zipPath = CreateTestZip("test.txt", "Hello, World!");

        // Act
        var result = ArchiveHandler.InspectArchive(zipPath);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.IsValid);
        Assert.Equal("test.txt", result.MainFileName);
        Assert.Equal(1, result.TotalTextFiles);
    }

    [Fact]
    public void InspectArchive_WithHtmlFile_PrefersHtmlOverTxt()
    {
        // Arrange
        var zipPath = CreateTestZipWithMultipleFiles(new Dictionary<string, string>
        {
            ["readme.txt"] = "Plain text content",
            ["index.html"] = "<html><body>HTML content</body></html>"
        });

        // Act
        var result = ArchiveHandler.InspectArchive(zipPath);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.IsValid);
        Assert.Equal("index.html", result.MainFileName);
    }

    [Fact]
    public void InspectArchive_WithGutenbergNaming_DetectsGutenberg()
    {
        // Arrange
        var zipPath = CreateTestZipWithMultipleFiles(new Dictionary<string, string>
        {
            ["pg1234.html"] = "<html><body>*** START OF THE PROJECT GUTENBERG EBOOK ***</body></html>"
        });

        // Act
        var result = ArchiveHandler.InspectArchive(zipPath);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.IsValid);
        Assert.True(result.IsGutenberg);
    }

    [Fact]
    public void InspectArchive_WithEmptyZip_ReturnsInvalid()
    {
        // Arrange
        var zipPath = Path.Combine(_tempDir, "empty.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            // Create empty zip
        }

        // Act
        var result = ArchiveHandler.InspectArchive(zipPath);

        // Assert
        Assert.NotNull(result);
        Assert.False(result.IsValid);
        Assert.Contains("No text files", result.Error);
    }

    [Fact]
    public void InspectArchive_WithOnlyBinaryFiles_ReturnsInvalid()
    {
        // Arrange
        var zipPath = CreateTestZipWithMultipleFiles(new Dictionary<string, string>
        {
            ["image.png"] = "fake png data",
            ["document.pdf"] = "fake pdf data"
        });

        // Act
        var result = ArchiveHandler.InspectArchive(zipPath);

        // Assert
        Assert.NotNull(result);
        Assert.False(result.IsValid);
        Assert.Contains("No text files", result.Error);
    }

    [Fact]
    public async Task ExtractTextAsync_WithValidZip_ReturnsContent()
    {
        // Arrange
        var content = "Hello, this is test content for extraction.";
        var zipPath = CreateTestZip("test.txt", content);

        // Act
        var result = await ArchiveHandler.ExtractTextAsync(zipPath);

        // Assert
        Assert.Equal(content, result);
    }

    [Fact]
    public async Task ExtractTextAsync_WithHtmlContent_ConvertsToMarkdown()
    {
        // Arrange
        var html = "<html><body><h1>Title</h1><p>Paragraph text.</p></body></html>";
        var zipPath = CreateTestZip("page.html", html);

        // Act
        var result = await ArchiveHandler.ExtractTextAsync(zipPath);

        // Assert
        Assert.Contains("# Title", result);
        Assert.Contains("Paragraph text", result);
        Assert.DoesNotContain("<html>", result);
        Assert.DoesNotContain("<body>", result);
    }

    [Fact]
    public async Task ExtractTextAsync_WithGutenbergBoilerplate_RemovesBoilerplate()
    {
        // Arrange
        var html = @"<html><body>
            <div>Project Gutenberg Header</div>
            *** START OF THE PROJECT GUTENBERG EBOOK ***
            <h1>The Actual Book</h1>
            <p>This is the real content.</p>
            *** END OF THE PROJECT GUTENBERG EBOOK ***
            <div>Project Gutenberg Footer</div>
        </body></html>";
        var zipPath = CreateTestZipWithMultipleFiles(new Dictionary<string, string>
        {
            ["pg1234.html"] = html
        });

        // Act
        var result = await ArchiveHandler.ExtractTextAsync(zipPath);

        // Assert
        Assert.Contains("The Actual Book", result);
        Assert.Contains("real content", result);
        // Boilerplate should be removed
        Assert.DoesNotContain("Project Gutenberg Header", result);
        Assert.DoesNotContain("Project Gutenberg Footer", result);
    }

    [Fact]
    public async Task ExtractTextAsync_WithInvalidZip_ThrowsException()
    {
        // Arrange
        var invalidPath = Path.Combine(_tempDir, "invalid.zip");
        File.WriteAllText(invalidPath, "Not a valid zip file");

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ArchiveHandler.ExtractTextAsync(invalidPath));
    }

    [Fact]
    public void InspectArchive_WithMarkdownFile_AcceptsMarkdown()
    {
        // Arrange
        var zipPath = CreateTestZip("readme.md", "# Markdown Title\n\nSome content here.");

        // Act
        var result = ArchiveHandler.InspectArchive(zipPath);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.IsValid);
        Assert.Equal("readme.md", result.MainFileName);
    }

    private string CreateTestZip(string fileName, string content)
    {
        var zipPath = Path.Combine(_tempDir, $"test_{Guid.NewGuid():N}.zip");
        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        var entry = archive.CreateEntry(fileName);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, Encoding.UTF8);
        writer.Write(content);
        return zipPath;
    }

    private string CreateTestZipWithMultipleFiles(Dictionary<string, string> files)
    {
        var zipPath = Path.Combine(_tempDir, $"test_{Guid.NewGuid():N}.zip");
        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var (fileName, content) in files)
        {
            var entry = archive.CreateEntry(fileName);
            using var stream = entry.Open();
            using var writer = new StreamWriter(stream, Encoding.UTF8);
            writer.Write(content);
        }
        return zipPath;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }
}
