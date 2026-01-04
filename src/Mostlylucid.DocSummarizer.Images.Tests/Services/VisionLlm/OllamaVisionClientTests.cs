using Xunit;
using Moq;
using Moq.Protected;
using System.Net;
using System.Net.Http.Json;
using Mostlylucid.DocSummarizer.Images.Services.VisionLlm;
using Microsoft.Extensions.Logging;

namespace Mostlylucid.DocSummarizer.Images.Tests.Services.VisionLlm;

public class OllamaVisionClientTests
{
    [Fact]
    public void Constructor_WithNullHttpClient_ShouldThrow()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            new OllamaVisionClient(null!, "model"));
    }

    [Fact]
    public async Task CheckAvailabilityAsync_WhenServiceResponds_ShouldReturnTrue()
    {
        // Arrange
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req => req.RequestUri!.PathAndQuery.Contains("/api/tags")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK
            });

        var httpClient = new HttpClient(mockHandler.Object)
        {
            BaseAddress = new Uri("http://localhost:11434")
        };

        var client = new OllamaVisionClient(httpClient);

        // Act
        var result = await client.CheckAvailabilityAsync();

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task CheckAvailabilityAsync_WhenServiceUnavailable_ShouldReturnFalse()
    {
        // Arrange
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Service unavailable"));

        var httpClient = new HttpClient(mockHandler.Object)
        {
            BaseAddress = new Uri("http://localhost:11434")
        };

        var client = new OllamaVisionClient(httpClient);

        // Act
        var result = await client.CheckAvailabilityAsync();

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task ExtractTextAsync_WithValidResponse_ShouldReturnText()
    {
        // Arrange
        var expectedText = "Hello World";
        var mockHandler = new Mock<HttpMessageHandler>();

        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req => req.RequestUri!.PathAndQuery.Contains("/api/generate")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = JsonContent.Create(new { response = expectedText, done = true })
            });

        var httpClient = new HttpClient(mockHandler.Object)
        {
            BaseAddress = new Uri("http://localhost:11434")
        };

        var client = new OllamaVisionClient(httpClient);

        // Create a temporary test image
        var testImagePath = Path.GetTempFileName();
        await File.WriteAllBytesAsync(testImagePath, new byte[] { 0x89, 0x50, 0x4E, 0x47 }); // PNG header

        try
        {
            // Act
            var result = await client.ExtractTextAsync(testImagePath);

            // Assert
            Assert.Equal(expectedText, result);
        }
        finally
        {
            File.Delete(testImagePath);
        }
    }

    [Fact]
    public async Task ExtractTextAsync_WithErrorResponse_ShouldReturnEmpty()
    {
        // Arrange
        var mockHandler = new Mock<HttpMessageHandler>();

        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.InternalServerError
            });

        var httpClient = new HttpClient(mockHandler.Object)
        {
            BaseAddress = new Uri("http://localhost:11434")
        };

        var client = new OllamaVisionClient(httpClient);

        var testImagePath = Path.GetTempFileName();
        await File.WriteAllBytesAsync(testImagePath, new byte[] { 0x89, 0x50, 0x4E, 0x47 });

        try
        {
            // Act
            var result = await client.ExtractTextAsync(testImagePath);

            // Assert
            Assert.Empty(result);
        }
        finally
        {
            File.Delete(testImagePath);
        }
    }

    [Fact]
    public async Task GenerateEmbeddingAsync_WithValidResponse_ShouldReturnVector()
    {
        // Arrange
        var expectedEmbedding = new[] { 0.1f, 0.2f, 0.3f };
        var mockHandler = new Mock<HttpMessageHandler>();

        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req => req.RequestUri!.PathAndQuery.Contains("/api/embeddings")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = JsonContent.Create(new { embedding = expectedEmbedding })
            });

        var httpClient = new HttpClient(mockHandler.Object)
        {
            BaseAddress = new Uri("http://localhost:11434")
        };

        var client = new OllamaVisionClient(httpClient);

        var testImagePath = Path.GetTempFileName();
        await File.WriteAllBytesAsync(testImagePath, new byte[] { 0x89, 0x50, 0x4E, 0x47 });

        try
        {
            // Act
            var result = await client.GenerateEmbeddingAsync(testImagePath);

            // Assert
            Assert.NotNull(result);
            Assert.Equal(expectedEmbedding.Length, result.Length);
        }
        finally
        {
            File.Delete(testImagePath);
        }
    }
}
