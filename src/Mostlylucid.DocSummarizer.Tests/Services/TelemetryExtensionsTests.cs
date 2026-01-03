using Microsoft.Extensions.DependencyInjection;
using Mostlylucid.DocSummarizer.Services;
using Xunit;

namespace Mostlylucid.DocSummarizer.Tests.Services;

/// <summary>
/// Tests for TelemetryExtensions and TelemetryOptions
/// </summary>
public class TelemetryExtensionsTests
{
    #region TelemetryOptions Tests

    [Fact]
    public void TelemetryOptions_DefaultValues_AreCorrect()
    {
        // Arrange & Act
        var options = new TelemetryOptions();

        // Assert
        Assert.False(options.Enabled);
        Assert.False(options.ConsoleExporter);
        Assert.Null(options.OtlpEndpoint);
        Assert.Equal("docsummarizer", options.ServiceName);
        Assert.Equal("4.0.0", options.ServiceVersion);
    }

    [Fact]
    public void TelemetryOptions_CanSetAllProperties()
    {
        // Arrange & Act
        var options = new TelemetryOptions
        {
            Enabled = true,
            ConsoleExporter = true,
            OtlpEndpoint = "http://localhost:4317",
            ServiceName = "custom-service",
            ServiceVersion = "1.0.0"
        };

        // Assert
        Assert.True(options.Enabled);
        Assert.True(options.ConsoleExporter);
        Assert.Equal("http://localhost:4317", options.OtlpEndpoint);
        Assert.Equal("custom-service", options.ServiceName);
        Assert.Equal("1.0.0", options.ServiceVersion);
    }

    #endregion

    #region ParseTelemetryOptions Tests

    [Fact]
    public void ParseTelemetryOptions_AllDisabled_ReturnsDisabled()
    {
        // Arrange & Act
        var options = TelemetryExtensions.ParseTelemetryOptions(
            telemetryEnabled: false,
            consoleExporter: false,
            otlpEndpoint: null);

        // Assert
        Assert.False(options.Enabled);
        Assert.False(options.ConsoleExporter);
        Assert.Null(options.OtlpEndpoint);
    }

    [Fact]
    public void ParseTelemetryOptions_TelemetryEnabled_EnablesOptions()
    {
        // Arrange & Act
        var options = TelemetryExtensions.ParseTelemetryOptions(
            telemetryEnabled: true,
            consoleExporter: false,
            otlpEndpoint: null);

        // Assert
        Assert.True(options.Enabled);
        Assert.False(options.ConsoleExporter);
        Assert.Null(options.OtlpEndpoint);
    }

    [Fact]
    public void ParseTelemetryOptions_ConsoleExporter_EnablesTelemetry()
    {
        // Arrange & Act
        var options = TelemetryExtensions.ParseTelemetryOptions(
            telemetryEnabled: false,
            consoleExporter: true,
            otlpEndpoint: null);

        // Assert
        Assert.True(options.Enabled); // Auto-enabled when console exporter is set
        Assert.True(options.ConsoleExporter);
        Assert.Null(options.OtlpEndpoint);
    }

    [Fact]
    public void ParseTelemetryOptions_OtlpEndpoint_EnablesTelemetry()
    {
        // Arrange & Act
        var options = TelemetryExtensions.ParseTelemetryOptions(
            telemetryEnabled: false,
            consoleExporter: false,
            otlpEndpoint: "http://localhost:4317");

        // Assert
        Assert.True(options.Enabled); // Auto-enabled when OTLP endpoint is set
        Assert.False(options.ConsoleExporter);
        Assert.Equal("http://localhost:4317", options.OtlpEndpoint);
    }

    [Fact]
    public void ParseTelemetryOptions_EmptyOtlpEndpoint_DoesNotEnableTelemetry()
    {
        // Arrange & Act
        var options = TelemetryExtensions.ParseTelemetryOptions(
            telemetryEnabled: false,
            consoleExporter: false,
            otlpEndpoint: "");

        // Assert
        Assert.False(options.Enabled);
        Assert.Equal("", options.OtlpEndpoint);
    }

    [Fact]
    public void ParseTelemetryOptions_AllEnabled_ReturnsAllEnabled()
    {
        // Arrange & Act
        var options = TelemetryExtensions.ParseTelemetryOptions(
            telemetryEnabled: true,
            consoleExporter: true,
            otlpEndpoint: "http://jaeger:4317");

        // Assert
        Assert.True(options.Enabled);
        Assert.True(options.ConsoleExporter);
        Assert.Equal("http://jaeger:4317", options.OtlpEndpoint);
    }

    [Theory]
    [InlineData("http://localhost:4317")]
    [InlineData("http://jaeger:4317")]
    [InlineData("http://grafana-agent:4317")]
    [InlineData("https://otel-collector.example.com:4317")]
    public void ParseTelemetryOptions_VariousOtlpEndpoints_AreParsedCorrectly(string endpoint)
    {
        // Arrange & Act
        var options = TelemetryExtensions.ParseTelemetryOptions(
            telemetryEnabled: false,
            consoleExporter: false,
            otlpEndpoint: endpoint);

        // Assert
        Assert.True(options.Enabled);
        Assert.Equal(endpoint, options.OtlpEndpoint);
    }

    #endregion

    #region AddDocSummarizerTelemetry Tests

    [Fact]
    public void AddDocSummarizerTelemetry_Disabled_DoesNotAddServices()
    {
        // Arrange
        var services = new ServiceCollection();
        var options = new TelemetryOptions { Enabled = false };

        // Act
        var result = services.AddDocSummarizerTelemetry(options);

        // Assert
        Assert.Same(services, result); // Returns same collection
        Assert.Empty(services); // No services added when disabled
    }

    [Fact]
    public void AddDocSummarizerTelemetry_Enabled_ReturnsServiceCollection()
    {
        // Arrange
        var services = new ServiceCollection();
        var options = new TelemetryOptions 
        { 
            Enabled = true,
            ConsoleExporter = true 
        };

        // Act
        var result = services.AddDocSummarizerTelemetry(options);

        // Assert
        Assert.Same(services, result);
        Assert.NotEmpty(services); // Services were added
    }

    [Fact]
    public void AddDocSummarizerTelemetry_WithOtlpEndpoint_AddsServices()
    {
        // Arrange
        var services = new ServiceCollection();
        var options = new TelemetryOptions 
        { 
            Enabled = true,
            OtlpEndpoint = "http://localhost:4317"
        };

        // Act
        var result = services.AddDocSummarizerTelemetry(options);

        // Assert
        Assert.Same(services, result);
        Assert.NotEmpty(services);
    }

    [Fact]
    public void AddDocSummarizerTelemetry_WithBothExporters_AddsServices()
    {
        // Arrange
        var services = new ServiceCollection();
        var options = new TelemetryOptions 
        { 
            Enabled = true,
            ConsoleExporter = true,
            OtlpEndpoint = "http://localhost:4317"
        };

        // Act
        var result = services.AddDocSummarizerTelemetry(options);

        // Assert
        Assert.Same(services, result);
        Assert.NotEmpty(services);
    }

    [Fact]
    public void AddDocSummarizerTelemetry_EnabledWithoutExporters_AddsServices()
    {
        // Arrange
        var services = new ServiceCollection();
        var options = new TelemetryOptions 
        { 
            Enabled = true,
            ConsoleExporter = false,
            OtlpEndpoint = null
        };

        // Act
        var result = services.AddDocSummarizerTelemetry(options);

        // Assert
        Assert.Same(services, result);
        // Even without exporters, OpenTelemetry infrastructure is set up
        Assert.NotEmpty(services);
    }

    [Fact]
    public void AddDocSummarizerTelemetry_CustomServiceInfo_UsesCustomValues()
    {
        // Arrange
        var services = new ServiceCollection();
        var options = new TelemetryOptions 
        { 
            Enabled = true,
            ConsoleExporter = true,
            ServiceName = "my-custom-service",
            ServiceVersion = "2.0.0"
        };

        // Act
        var result = services.AddDocSummarizerTelemetry(options);

        // Assert
        Assert.Same(services, result);
        Assert.NotEmpty(services);
    }

    #endregion
}
