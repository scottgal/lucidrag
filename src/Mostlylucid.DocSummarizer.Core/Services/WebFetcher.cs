using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Microsoft.Playwright;
using Mostlylucid.DocSummarizer.Config;
using Mostlylucid.DocSummarizer.Models;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Processing;

namespace Mostlylucid.DocSummarizer.Services;

/// <summary>
/// Security-hardened web content fetcher for document processing.
/// 
/// Security features:
/// - SSRF protection: blocks private IPs, localhost, link-local, cloud metadata endpoints
/// - DNS rebinding protection: re-validates IPs after redirects
/// - Request policy: redirect limits, size limits, timeouts, GET-only
/// - Content-type gating: only accepts safe document types
/// - Decompression bomb protection: limits expansion ratio
/// - HTML sanitization: removes scripts, event handlers, dangerous URLs
/// - Image guardrails: size limits, count limits, hash deduplication
/// 
/// Resilience features:
/// - Polly retry with exponential backoff and jitter
/// - Special handling for 429 (rate limited) with Retry-After header respect
/// - Circuit breaker for persistent failures
/// 
/// Observability:
/// - OpenTelemetry tracing with Activity spans
/// - Metrics for request counts, durations, and status codes
/// </summary>
public class WebFetcher
{
    private readonly WebFetchConfig _config;
    private readonly ResiliencePipeline<HttpResponseMessage> _resiliencePipeline;
    
    #region OpenTelemetry Instrumentation
    
    /// <summary>ActivitySource for distributed tracing</summary>
    private static readonly ActivitySource ActivitySource = new("Mostlylucid.DocSummarizer.WebFetcher", "1.0.0");
    
    /// <summary>Meter for metrics</summary>
    private static readonly Meter Meter = new("Mostlylucid.DocSummarizer.WebFetcher", "1.0.0");
    
    /// <summary>Counter for total fetch requests</summary>
    private static readonly Counter<long> FetchRequestsCounter = Meter.CreateCounter<long>(
        "docsummarizer.webfetch.requests",
        "requests",
        "Total number of web fetch requests");
    
    /// <summary>Counter for fetch errors by type</summary>
    private static readonly Counter<long> FetchErrorsCounter = Meter.CreateCounter<long>(
        "docsummarizer.webfetch.errors",
        "errors",
        "Total number of web fetch errors");
    
    /// <summary>Histogram for fetch duration</summary>
    private static readonly Histogram<double> FetchDurationHistogram = Meter.CreateHistogram<double>(
        "docsummarizer.webfetch.duration",
        "ms",
        "Duration of web fetch operations in milliseconds");
    
    /// <summary>Counter for retries</summary>
    private static readonly Counter<long> RetryCounter = Meter.CreateCounter<long>(
        "docsummarizer.webfetch.retries",
        "retries",
        "Total number of retry attempts");
    
    /// <summary>Counter for rate limit hits (429 responses)</summary>
    private static readonly Counter<long> RateLimitCounter = Meter.CreateCounter<long>(
        "docsummarizer.webfetch.ratelimits",
        "responses",
        "Total number of 429 rate limit responses");
    
    #endregion

    #region Security Constants

    /// <summary>Maximum redirects to follow (prevents infinite loops)</summary>
    private const int MaxRedirects = 5;

    /// <summary>Maximum response size in bytes (10MB)</summary>
    private const int MaxResponseBytes = 10 * 1024 * 1024;

    /// <summary>Maximum HTML size in bytes (5MB)</summary>
    private const int MaxHtmlBytes = 5 * 1024 * 1024;

    /// <summary>Maximum decompression ratio (prevents zip bombs)</summary>
    private const int MaxDecompressionRatio = 20;

    /// <summary>Maximum images to process per document</summary>
    private const int MaxImagesPerDocument = 50;

    /// <summary>Maximum image dimension (width or height) before resizing</summary>
    private const int MaxImageDimension = 1920;

    /// <summary>Maximum pixel area (prevents memory exhaustion: 4K resolution)</summary>
    private const int MaxImagePixelArea = 3840 * 2160;

    /// <summary>Maximum file size in bytes before compression (2MB)</summary>
    private const int MaxImageFileSize = 2 * 1024 * 1024;

    /// <summary>JPEG quality for compressed images (0-100)</summary>
    private const int JpegQuality = 75;

    /// <summary>
    /// Cloud metadata IP addresses to block (SSRF targets)
    /// </summary>
    private static readonly IPAddress[] BlockedMetadataIPs =
    {
        IPAddress.Parse("169.254.169.254"), // AWS, GCP, Azure metadata
        IPAddress.Parse("169.254.170.2"),   // AWS ECS task metadata
        IPAddress.Parse("fd00:ec2::254"),   // AWS IPv6 metadata
    };

    /// <summary>
    /// Private/reserved IP ranges to block
    /// </summary>
    private static readonly (IPAddress Network, int PrefixLength)[] BlockedIPRanges =
    {
        // IPv4 private ranges
        (IPAddress.Parse("10.0.0.0"), 8),       // Class A private
        (IPAddress.Parse("172.16.0.0"), 12),    // Class B private
        (IPAddress.Parse("192.168.0.0"), 16),   // Class C private
        (IPAddress.Parse("127.0.0.0"), 8),      // Loopback
        (IPAddress.Parse("169.254.0.0"), 16),   // Link-local
        (IPAddress.Parse("0.0.0.0"), 8),        // Current network
        (IPAddress.Parse("224.0.0.0"), 4),      // Multicast
        (IPAddress.Parse("240.0.0.0"), 4),      // Reserved
        
        // IPv6 private ranges  
        (IPAddress.Parse("::1"), 128),          // Loopback
        (IPAddress.Parse("fc00::"), 7),         // Unique local
        (IPAddress.Parse("fe80::"), 10),        // Link-local
        (IPAddress.Parse("::ffff:0:0"), 96),    // IPv4-mapped (check the IPv4 part separately)
    };

    /// <summary>
    /// Allowed content types for fetching
    /// </summary>
    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        // Documents
        "text/html",
        "application/xhtml+xml",
        "application/pdf",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        
        // Text formats
        "text/plain",
        "text/markdown",
        "text/x-markdown",
        "text/csv",
        "text/xml",
        "application/json",
        "application/xml",
        
        // Images (for OCR)
        "image/png",
        "image/jpeg",
        "image/tiff",
        "image/webp",
        // Note: image/gif and image/svg+xml intentionally excluded (svg can contain scripts)
    };

    /// <summary>
    /// Content types mapped to file extensions
    /// </summary>
    private static readonly Dictionary<string, string> ContentTypeToExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        ["application/pdf"] = ".pdf",
        ["application/vnd.openxmlformats-officedocument.wordprocessingml.document"] = ".docx",
        ["application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"] = ".xlsx",
        ["application/vnd.openxmlformats-officedocument.presentationml.presentation"] = ".pptx",
        ["text/html"] = ".html",
        ["application/xhtml+xml"] = ".html",
        ["text/plain"] = ".txt",
        ["text/markdown"] = ".md",
        ["text/x-markdown"] = ".md",
        ["application/json"] = ".json",
        ["text/json"] = ".json",
        ["application/xml"] = ".xml",
        ["text/xml"] = ".xml",
        ["text/csv"] = ".csv",
        ["image/png"] = ".png",
        ["image/jpeg"] = ".jpg",
        ["image/tiff"] = ".tiff",
        ["image/webp"] = ".webp",
    };

    /// <summary>
    /// Dangerous HTML elements to remove
    /// </summary>
    private static readonly string[] DangerousElements =
    {
        "script", "noscript", "iframe", "frame", "frameset",
        "object", "embed", "applet", "form", "input", "button",
        "select", "textarea", "link", "style", "meta", "base",
        "svg", "math", "template", "slot", "portal"
    };

    /// <summary>
    /// Dangerous URL schemes
    /// </summary>
    private static readonly HashSet<string> DangerousSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "javascript", "vbscript", "data", "blob", "file"
    };

    #endregion
    
    #region Resilience Configuration
    
    /// <summary>Maximum retry attempts for transient failures</summary>
    private const int MaxRetryAttempts = 3;
    
    /// <summary>Initial retry delay in milliseconds</summary>
    private const int InitialRetryDelayMs = 500;
    
    /// <summary>Maximum retry delay in milliseconds</summary>
    private const int MaxRetryDelayMs = 30000;
    
    /// <summary>HTTP status codes that should trigger a retry</summary>
    private static readonly HashSet<HttpStatusCode> RetryableStatusCodes = new()
    {
        HttpStatusCode.RequestTimeout,           // 408
        HttpStatusCode.TooManyRequests,          // 429
        HttpStatusCode.InternalServerError,      // 500
        HttpStatusCode.BadGateway,               // 502
        HttpStatusCode.ServiceUnavailable,       // 503
        HttpStatusCode.GatewayTimeout            // 504
    };
    
    /// <summary>HTTP status codes that are permanent failures (no retry)</summary>
    private static readonly HashSet<HttpStatusCode> PermanentFailureStatusCodes = new()
    {
        HttpStatusCode.BadRequest,               // 400
        HttpStatusCode.Unauthorized,             // 401
        HttpStatusCode.PaymentRequired,          // 402
        HttpStatusCode.Forbidden,                // 403
        HttpStatusCode.NotFound,                 // 404
        HttpStatusCode.MethodNotAllowed,         // 405
        HttpStatusCode.Gone                      // 410
    };
    
    /// <summary>Circuit breaker failure threshold before opening</summary>
    private const int CircuitBreakerFailureThreshold = 5;
    
    /// <summary>Sampling duration for circuit breaker</summary>
    private static readonly TimeSpan CircuitBreakerSamplingDuration = TimeSpan.FromSeconds(30);
    
    /// <summary>Duration circuit stays open before half-open state</summary>
    private static readonly TimeSpan CircuitBreakerBreakDuration = TimeSpan.FromSeconds(60);
    
    /// <summary>Counter for circuit breaker state changes</summary>
    private static readonly Counter<long> CircuitBreakerCounter = Meter.CreateCounter<long>(
        "docsummarizer.webfetch.circuit_breaker",
        "transitions",
        "Circuit breaker state transitions");
    
    #endregion

    public WebFetcher(WebFetchConfig config)
    {
        _config = config;
        _resiliencePipeline = BuildResiliencePipeline();
    }
    
    /// <summary>
    /// Build Polly resilience pipeline with circuit breaker and retry logic.
    /// Pipeline order: CircuitBreaker -> Retry
    /// - Circuit breaker opens after repeated failures to fail fast and protect downstream services
    /// - Retry handles transient failures with exponential backoff
    /// - Special handling for 429 with Retry-After header respect
    /// </summary>
    private ResiliencePipeline<HttpResponseMessage> BuildResiliencePipeline()
    {
        return new ResiliencePipelineBuilder<HttpResponseMessage>()
            // Circuit breaker - opens after repeated failures to fail fast
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions<HttpResponseMessage>
            {
                FailureRatio = 0.5, // Open circuit if 50% of requests fail
                MinimumThroughput = CircuitBreakerFailureThreshold,
                SamplingDuration = CircuitBreakerSamplingDuration,
                BreakDuration = CircuitBreakerBreakDuration,
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .Handle<HttpRequestException>()
                    .Handle<TaskCanceledException>(ex => !ex.CancellationToken.IsCancellationRequested)
                    .HandleResult(response => RetryableStatusCodes.Contains(response.StatusCode)),
                OnOpened = args =>
                {
                    CircuitBreakerCounter.Add(1, new KeyValuePair<string, object?>("state", "opened"));
                    Console.WriteLine($"[WebFetch] Circuit breaker OPENED - requests will fail fast for {args.BreakDuration.TotalSeconds:F0}s");
                    return ValueTask.CompletedTask;
                },
                OnClosed = _ =>
                {
                    CircuitBreakerCounter.Add(1, new KeyValuePair<string, object?>("state", "closed"));
                    Console.WriteLine("[WebFetch] Circuit breaker CLOSED - normal operation resumed");
                    return ValueTask.CompletedTask;
                },
                OnHalfOpened = _ =>
                {
                    CircuitBreakerCounter.Add(1, new KeyValuePair<string, object?>("state", "half-opened"));
                    Console.WriteLine("[WebFetch] Circuit breaker HALF-OPEN - testing with probe request");
                    return ValueTask.CompletedTask;
                }
            })
            // Retry - handles transient failures with exponential backoff
            .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                MaxRetryAttempts = MaxRetryAttempts,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true, // Decorrelated jitter for distributed systems
                Delay = TimeSpan.FromMilliseconds(InitialRetryDelayMs),
                MaxDelay = TimeSpan.FromMilliseconds(MaxRetryDelayMs),
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .Handle<HttpRequestException>()
                    .Handle<TaskCanceledException>(ex => !ex.CancellationToken.IsCancellationRequested)
                    .HandleResult(response => RetryableStatusCodes.Contains(response.StatusCode)),
                DelayGenerator = args =>
                {
                    // Special handling for 429 - respect Retry-After header
                    if (args.Outcome.Result?.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        RateLimitCounter.Add(1, new KeyValuePair<string, object?>("url.host", args.Context.Properties.GetValue(new ResiliencePropertyKey<string>("host"), "unknown")));
                        
                        var retryAfter = args.Outcome.Result.Headers.RetryAfter;
                        if (retryAfter?.Delta != null)
                        {
                            return ValueTask.FromResult<TimeSpan?>(retryAfter.Delta.Value);
                        }
                        if (retryAfter?.Date != null)
                        {
                            var delay = retryAfter.Date.Value - DateTimeOffset.UtcNow;
                            if (delay > TimeSpan.Zero && delay <= TimeSpan.FromMinutes(5))
                            {
                                return ValueTask.FromResult<TimeSpan?>(delay);
                            }
                        }
                    }
                    // Return null to use default exponential backoff
                    return ValueTask.FromResult<TimeSpan?>(null);
                },
                OnRetry = args =>
                {
                    RetryCounter.Add(1, new KeyValuePair<string, object?>("attempt", args.AttemptNumber));
                    
                    var statusCode = args.Outcome.Result?.StatusCode.ToString() ?? "Exception";
                    var message = args.Outcome.Exception?.Message ?? $"HTTP {statusCode}";
                    
                    Console.WriteLine($"[WebFetch] Retry {args.AttemptNumber}/{MaxRetryAttempts} after {args.RetryDelay.TotalSeconds:F1}s: {message}");
                    return ValueTask.CompletedTask;
                }
            })
            .Build();
    }

    /// <summary>
    /// Fetch web content with full security validation.
    /// Returns path to temporary file for processing.
    /// </summary>
    public async Task<WebFetchResult> FetchAsync(string url, WebFetchMode mode)
    {
        using var activity = ActivitySource.StartActivity("WebFetch", ActivityKind.Client);
        activity?.SetTag("url.full", url);
        activity?.SetTag("webfetch.mode", mode.ToString());
        
        var stopwatch = Stopwatch.StartNew();
        var host = "unknown";
        
        try
        {
            if (!_config.Enabled)
            {
                throw new InvalidOperationException(
                    "Web fetch is not enabled. Use --web-enabled flag or set WebFetch.Enabled = true in config.");
            }

            // Validate URL structure
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                throw new ArgumentException($"Invalid URL: {url}");
            }
            
            host = uri.Host;
            activity?.SetTag("url.host", host);
            activity?.SetTag("url.scheme", uri.Scheme);

            // Validate scheme (only http/https)
            if (uri.Scheme != "http" && uri.Scheme != "https")
            {
                throw new SecurityException($"Blocked scheme '{uri.Scheme}': only HTTP/HTTPS allowed");
            }

            // Validate host is not blocked (SSRF protection)
            await ValidateHostAsync(uri);
            
            FetchRequestsCounter.Add(1, 
                new KeyValuePair<string, object?>("mode", mode.ToString()),
                new KeyValuePair<string, object?>("url.host", host));

            var result = mode switch
            {
                WebFetchMode.Simple => await FetchWithSecurityAsync(uri),
                WebFetchMode.Playwright => await FetchWithPlaywrightAsync(uri),
                _ => throw new ArgumentOutOfRangeException(nameof(mode), $"Unsupported mode: {mode}")
            };
            
            activity?.SetTag("http.response.content_type", result.ContentType);
            activity?.SetTag("webfetch.file_extension", result.FileExtension);
            activity?.SetStatus(ActivityStatusCode.Ok);
            
            return result;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.SetTag("error.type", ex.GetType().Name);
            
            var errorType = ex switch
            {
                SecurityException => "security",
                HttpRequestException => "http",
                TaskCanceledException => "timeout",
                InvalidOperationException => "operation",
                ArgumentException => "argument",
                _ => "unknown"
            };
            
            FetchErrorsCounter.Add(1,
                new KeyValuePair<string, object?>("error.type", errorType),
                new KeyValuePair<string, object?>("url.host", host));
            
            throw;
        }
        finally
        {
            stopwatch.Stop();
            FetchDurationHistogram.Record(stopwatch.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("mode", mode.ToString()),
                new KeyValuePair<string, object?>("url.host", host));
        }
    }

    #region SSRF Protection

    /// <summary>
    /// Validate that the host is not a private/internal address (SSRF protection)
    /// </summary>
    private async Task ValidateHostAsync(Uri uri)
    {
        IPAddress[] addresses;
        
        try
        {
            // Resolve DNS to get actual IP addresses
            addresses = await Dns.GetHostAddressesAsync(uri.Host);
        }
        catch (SocketException)
        {
            throw new SecurityException($"Cannot resolve host: {uri.Host}");
        }

        if (addresses.Length == 0)
        {
            throw new SecurityException($"No addresses found for host: {uri.Host}");
        }

        foreach (var address in addresses)
        {
            ValidateIPAddress(address, uri.Host);
        }
    }

    /// <summary>
    /// Validate that an IP address is not private/internal
    /// </summary>
    private static void ValidateIPAddress(IPAddress address, string host)
    {
        // Check explicit metadata IPs
        foreach (var blocked in BlockedMetadataIPs)
        {
            if (address.Equals(blocked))
            {
                throw new SecurityException($"Blocked metadata endpoint: {host} resolves to {address}");
            }
        }

        // Check blocked IP ranges
        foreach (var (network, prefixLength) in BlockedIPRanges)
        {
            if (IsInRange(address, network, prefixLength))
            {
                throw new SecurityException($"Blocked private/reserved IP: {host} resolves to {address}");
            }
        }

        // Additional check for IPv4-mapped IPv6 addresses
        if (address.IsIPv4MappedToIPv6)
        {
            var ipv4 = address.MapToIPv4();
            ValidateIPAddress(ipv4, host);
        }
    }

    /// <summary>
    /// Check if an IP address is within a CIDR range
    /// </summary>
    private static bool IsInRange(IPAddress address, IPAddress network, int prefixLength)
    {
        if (address.AddressFamily != network.AddressFamily)
            return false;

        var addressBytes = address.GetAddressBytes();
        var networkBytes = network.GetAddressBytes();

        var fullBytes = prefixLength / 8;
        var remainingBits = prefixLength % 8;

        for (var i = 0; i < fullBytes; i++)
        {
            if (addressBytes[i] != networkBytes[i])
                return false;
        }

        if (remainingBits > 0 && fullBytes < addressBytes.Length)
        {
            var mask = (byte)(0xFF << (8 - remainingBits));
            if ((addressBytes[fullBytes] & mask) != (networkBytes[fullBytes] & mask))
                return false;
        }

        return true;
    }

    #endregion

    #region Secure Fetch

    /// <summary>
    /// Fetch with security controls: redirect limits, size limits, content-type validation.
    /// Uses Polly resilience pipeline for retries on transient failures and rate limiting.
    /// </summary>
    private async Task<WebFetchResult> FetchWithSecurityAsync(Uri originalUri)
    {
        using var activity = ActivitySource.StartActivity("FetchWithSecurity", ActivityKind.Client);
        activity?.SetTag("url.original", originalUri.ToString());
        
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false, // Handle redirects manually for security
            AutomaticDecompression = DecompressionMethods.None, // Handle decompression manually
        };

        using var httpClient = new HttpClient(handler);
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(_config.UserAgent);
        httpClient.Timeout = TimeSpan.FromSeconds(_config.TimeoutSeconds);
        
        // Don't send cookies or auth
        httpClient.DefaultRequestHeaders.Remove("Cookie");
        httpClient.DefaultRequestHeaders.Remove("Authorization");

        var currentUri = originalUri;
        var redirectCount = 0;

        while (true)
        {
            // Re-validate after each redirect (DNS rebinding protection)
            if (redirectCount > 0)
            {
                await ValidateHostAsync(currentUri);
            }

            HttpResponseMessage response;
            try
            {
                // Execute with Polly resilience pipeline (retry on transient failures, rate limiting)
                response = await _resiliencePipeline.ExecuteAsync(
                    async token =>
                    {
                        // Clone request for retry (HttpRequestMessage can only be sent once)
                        using var clonedRequest = new HttpRequestMessage(HttpMethod.Get, currentUri);
                        clonedRequest.Headers.Accept.Clear();
                        clonedRequest.Headers.Accept.ParseAdd("text/html, application/xhtml+xml, application/pdf, text/plain, text/markdown, application/json, image/png, image/jpeg, image/tiff, image/webp");
                        
                        var resp = await httpClient.SendAsync(clonedRequest, HttpCompletionOption.ResponseHeadersRead, token);
                        
                        // For non-redirect responses, check if we should handle status code errors
                        if ((int)resp.StatusCode >= 400)
                        {
                            // Permanent failures - throw immediately without retry
                            if (PermanentFailureStatusCodes.Contains(resp.StatusCode))
                            {
                                var errorMessage = resp.StatusCode switch
                                {
                                    HttpStatusCode.Forbidden => $"Access forbidden (403): {currentUri}. The server is blocking access - this may require authentication or the resource is restricted.",
                                    HttpStatusCode.Unauthorized => $"Unauthorized (401): {currentUri}. Authentication is required.",
                                    HttpStatusCode.NotFound => $"Not found (404): {currentUri}. The resource does not exist.",
                                    HttpStatusCode.Gone => $"Gone (410): {currentUri}. The resource has been permanently removed.",
                                    _ => $"HTTP {(int)resp.StatusCode} ({resp.StatusCode}): {currentUri}"
                                };
                                throw new WebFetchPermanentException(errorMessage, resp.StatusCode);
                            }
                        }
                        
                        return resp;
                    }, 
                    CancellationToken.None);
            }
            catch (WebFetchPermanentException ex)
            {
                // Re-throw permanent failures with clear message
                activity?.SetTag("http.response.status_code", (int)ex.StatusCode);
                throw new HttpRequestException(ex.Message, ex, ex.StatusCode);
            }
            catch (HttpRequestException ex)
            {
                throw new InvalidOperationException($"Failed to fetch {currentUri}: {ex.Message}", ex);
            }

            activity?.SetTag("http.response.status_code", (int)response.StatusCode);

            // Handle redirects manually
            if ((int)response.StatusCode >= 300 && (int)response.StatusCode < 400)
            {
                redirectCount++;
                activity?.SetTag("http.redirect_count", redirectCount);
                
                if (redirectCount > MaxRedirects)
                {
                    throw new SecurityException($"Too many redirects ({MaxRedirects} max)");
                }

                var location = response.Headers.Location;
                if (location == null)
                {
                    throw new InvalidOperationException("Redirect without Location header");
                }

                // Resolve relative redirects
                currentUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);

                // Block cross-scheme redirects (https -> http downgrade)
                if (originalUri.Scheme == "https" && currentUri.Scheme == "http")
                {
                    throw new SecurityException("Blocked HTTPS to HTTP redirect (protocol downgrade)");
                }

                // Validate scheme again
                if (currentUri.Scheme != "http" && currentUri.Scheme != "https")
                {
                    throw new SecurityException($"Blocked redirect to scheme '{currentUri.Scheme}'");
                }

                response.Dispose();
                continue;
            }

            response.EnsureSuccessStatusCode();

            // Validate content type
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
            if (!IsAllowedContentType(contentType))
            {
                throw new SecurityException($"Blocked content type: {contentType}");
            }

            // Validate content length
            var contentLength = response.Content.Headers.ContentLength;
            if (contentLength > MaxResponseBytes)
            {
                throw new SecurityException($"Response too large: {contentLength} bytes (max {MaxResponseBytes})");
            }

            // Read content with size limit
            var content = await ReadContentWithLimitAsync(response);

            // Process based on content type
            return await ProcessResponseAsync(content, contentType, currentUri);
        }
    }

    /// <summary>
    /// Read response content with size limit and decompression bomb protection
    /// </summary>
    private async Task<byte[]> ReadContentWithLimitAsync(HttpResponseMessage response)
    {
        var contentLength = response.Content.Headers.ContentLength ?? 0;
        var maxSize = contentLength > 0 ? Math.Min(contentLength, MaxResponseBytes) : MaxResponseBytes;

        using var stream = await response.Content.ReadAsStreamAsync();
        using var memoryStream = new MemoryStream();
        
        var buffer = new byte[8192];
        var totalRead = 0L;
        int bytesRead;

        while ((bytesRead = await stream.ReadAsync(buffer)) > 0)
        {
            totalRead += bytesRead;
            
            if (totalRead > MaxResponseBytes)
            {
                throw new SecurityException($"Response exceeded size limit ({MaxResponseBytes} bytes)");
            }

            // Check decompression ratio if we have content-length
            if (contentLength > 0 && totalRead > contentLength * MaxDecompressionRatio)
            {
                throw new SecurityException("Possible decompression bomb detected");
            }

            await memoryStream.WriteAsync(buffer.AsMemory(0, bytesRead));
        }

        return memoryStream.ToArray();
    }

    /// <summary>
    /// Check if content type is allowed
    /// </summary>
    private static bool IsAllowedContentType(string contentType)
    {
        var ct = contentType.Split(';')[0].Trim();
        return AllowedContentTypes.Contains(ct);
    }

    #endregion

    #region Content Processing

    /// <summary>
    /// Process response based on content type
    /// </summary>
    private async Task<WebFetchResult> ProcessResponseAsync(byte[] content, string contentType, Uri sourceUri)
    {
        var ct = contentType.Split(';')[0].Trim();
        var extension = GetExtensionForContentType(ct, sourceUri.AbsolutePath);

        // Binary content (PDF, Office docs)
        if (IsBinaryDocument(extension))
        {
            var tempFile = CreateTempFile(extension);
            await File.WriteAllBytesAsync(tempFile, content);
            return new WebFetchResult(tempFile, contentType, sourceUri.ToString(), extension, isHtmlContent: false);
        }

        // Image content
        if (IsImageContent(extension))
        {
            var (processedBytes, newExtension) = await ProcessImageAsync(content, extension);
            var tempFile = CreateTempFile(newExtension);
            await File.WriteAllBytesAsync(tempFile, processedBytes);
            return new WebFetchResult(tempFile, contentType, sourceUri.ToString(), newExtension, isHtmlContent: false);
        }

        // Text content
        var textContent = Encoding.UTF8.GetString(content);

        // HTML needs sanitization
        if (extension == ".html" || ct.Contains("html"))
        {
            if (content.Length > MaxHtmlBytes)
            {
                throw new SecurityException($"HTML too large: {content.Length} bytes (max {MaxHtmlBytes})");
            }
            
            var sanitized = await SanitizeHtmlAsync(textContent, sourceUri);
            var tempFile = CreateTempFile(".html");
            await File.WriteAllTextAsync(tempFile, sanitized);
            return new WebFetchResult(tempFile, contentType, sourceUri.ToString(), ".html", isHtmlContent: true);
        }

        // Other text formats - pass through
        var textTempFile = CreateTempFile(extension);
        await File.WriteAllTextAsync(textTempFile, textContent);
        return new WebFetchResult(textTempFile, contentType, sourceUri.ToString(), extension, isHtmlContent: false);
    }

    private static string CreateTempFile(string extension)
    {
        return Path.Combine(Path.GetTempPath(), $"webfetch_{Guid.NewGuid():N}{extension}");
    }

    private static bool IsBinaryDocument(string extension)
    {
        return extension is ".pdf" or ".docx" or ".xlsx" or ".pptx";
    }

    private static bool IsImageContent(string extension)
    {
        return extension is ".png" or ".jpg" or ".jpeg" or ".tiff" or ".webp";
    }

    private static string GetExtensionForContentType(string contentType, string urlPath)
    {
        if (ContentTypeToExtension.TryGetValue(contentType, out var ext))
            return ext;

        // Try URL extension
        var urlExt = Path.GetExtension(urlPath);
        if (!string.IsNullOrEmpty(urlExt) && urlExt.Length <= 5)
            return urlExt.ToLowerInvariant();

        // Default based on content type category
        if (contentType.StartsWith("text/")) return ".txt";
        if (contentType.StartsWith("image/")) return ".png";

        return ".html";
    }

    #endregion

    #region Image Processing

    /// <summary>
    /// Process image with security validation and resizing
    /// </summary>
    private async Task<(byte[] bytes, string extension)> ProcessImageAsync(byte[] imageBytes, string extension)
    {
        try
        {
            using var inputStream = new MemoryStream(imageBytes);
            using var image = await Image.LoadAsync(inputStream);

            // Validate pixel area (memory exhaustion protection)
            var pixelArea = (long)image.Width * image.Height;
            if (pixelArea > MaxImagePixelArea)
            {
                throw new SecurityException($"Image too large: {image.Width}x{image.Height} ({pixelArea} pixels, max {MaxImagePixelArea})");
            }

            var originalWidth = image.Width;
            var originalHeight = image.Height;
            var originalSize = imageBytes.Length;

            // Check if resizing is needed
            var needsResize = originalWidth > MaxImageDimension || originalHeight > MaxImageDimension;
            var needsCompression = originalSize > MaxImageFileSize;

            if (!needsResize && !needsCompression)
            {
                return (imageBytes, extension);
            }

            // Calculate new dimensions maintaining aspect ratio
            var scale = 1.0;
            if (needsResize)
            {
                var widthScale = (double)MaxImageDimension / originalWidth;
                var heightScale = (double)MaxImageDimension / originalHeight;
                scale = Math.Min(Math.Min(widthScale, heightScale), 1.0);
            }

            var newWidth = (int)(originalWidth * scale);
            var newHeight = (int)(originalHeight * scale);

            if (newWidth != originalWidth || newHeight != originalHeight)
            {
                image.Mutate(x => x.Resize(newWidth, newHeight));
            }

            // Save with compression
            using var outputStream = new MemoryStream();

            if (extension == ".png" && originalSize < MaxImageFileSize / 2)
            {
                await image.SaveAsPngAsync(outputStream, new PngEncoder
                {
                    CompressionLevel = PngCompressionLevel.BestCompression
                });
                return (outputStream.ToArray(), ".png");
            }
            else
            {
                await image.SaveAsJpegAsync(outputStream, new JpegEncoder
                {
                    Quality = JpegQuality
                });
                return (outputStream.ToArray(), ".jpg");
            }
        }
        catch (SecurityException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [Image] Warning: Could not process image ({ex.Message}), using original");
            return (imageBytes, extension);
        }
    }

    #endregion

    #region HTML Sanitization

    /// <summary>
    /// Thoroughly sanitize HTML removing all dangerous content
    /// </summary>
    private async Task<string> SanitizeHtmlAsync(string html, Uri baseUri)
    {
        var config = AngleSharp.Configuration.Default;
        var context = BrowsingContext.New(config);
        var parser = context.GetService<IHtmlParser>()
                    ?? throw new InvalidOperationException("Failed to create HTML parser");

        var document = await parser.ParseDocumentAsync(html);

        // Remove dangerous elements
        foreach (var selector in DangerousElements)
        {
            foreach (var element in document.QuerySelectorAll(selector).ToList())
            {
                element.Remove();
            }
        }

        // Process all elements
        var imageCount = 0;
        var seenImageHashes = new HashSet<string>();

        foreach (var element in document.QuerySelectorAll("*").ToList())
        {
            // Remove all event handlers (on*)
            var attrsToRemove = element.Attributes
                .Where(a => a.Name.StartsWith("on", StringComparison.OrdinalIgnoreCase))
                .Select(a => a.Name)
                .ToList();

            foreach (var attr in attrsToRemove)
            {
                element.RemoveAttribute(attr);
            }

            // Remove dangerous href/src schemes
            SanitizeUrlAttribute(element, "href");
            SanitizeUrlAttribute(element, "src");
            SanitizeUrlAttribute(element, "action");
            SanitizeUrlAttribute(element, "formaction");
            SanitizeUrlAttribute(element, "xlink:href");
            SanitizeUrlAttribute(element, "poster");
            SanitizeUrlAttribute(element, "data");

            // Remove style attributes (can contain expressions)
            element.RemoveAttribute("style");
        }

        // Process images specifically
        foreach (var img in document.QuerySelectorAll("img").ToList())
        {
            imageCount++;
            if (imageCount > MaxImagesPerDocument)
            {
                img.Remove();
                continue;
            }

            var src = img.GetAttribute("src");
            if (string.IsNullOrEmpty(src))
            {
                // Try data-src for lazy loading
                src = img.GetAttribute("data-src");
            }

            if (string.IsNullOrEmpty(src))
            {
                img.Remove();
                continue;
            }

            // Convert to absolute URL and validate
            try
            {
                var absoluteUri = new Uri(baseUri, src);
                
                // Only allow http/https image sources
                if (absoluteUri.Scheme != "http" && absoluteUri.Scheme != "https")
                {
                    img.Remove();
                    continue;
                }

                // Deduplicate by URL hash
                var urlHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(absoluteUri.AbsoluteUri)))[..16];
                if (!seenImageHashes.Add(urlHash))
                {
                    img.Remove();
                    continue;
                }

                img.SetAttribute("src", absoluteUri.AbsoluteUri);
            }
            catch
            {
                img.Remove();
                continue;
            }

            // Clean up image attributes
            img.RemoveAttribute("srcset");
            img.RemoveAttribute("data-srcset");
            img.RemoveAttribute("data-src");
            img.RemoveAttribute("data-lazy");
            img.RemoveAttribute("loading");
            img.RemoveAttribute("onerror");
            img.RemoveAttribute("onload");
        }

        // Process links - convert to absolute but mark as non-navigable
        foreach (var link in document.QuerySelectorAll("a[href]").ToList())
        {
            var href = link.GetAttribute("href");
            if (string.IsNullOrEmpty(href) || href.StartsWith("#"))
                continue;

            try
            {
                var absoluteUri = new Uri(baseUri, href);
                if (absoluteUri.Scheme == "http" || absoluteUri.Scheme == "https")
                {
                    link.SetAttribute("href", absoluteUri.AbsoluteUri);
                }
                else
                {
                    link.RemoveAttribute("href");
                }
            }
            catch
            {
                link.RemoveAttribute("href");
            }
        }

        return document.DocumentElement?.OuterHtml ?? html;
    }

    /// <summary>
    /// Sanitize a URL attribute, removing dangerous schemes
    /// </summary>
    private static void SanitizeUrlAttribute(IElement element, string attributeName)
    {
        var value = element.GetAttribute(attributeName);
        if (string.IsNullOrEmpty(value))
            return;

        value = value.Trim();

        // Check for dangerous schemes
        var colonIndex = value.IndexOf(':');
        if (colonIndex > 0 && colonIndex < 20) // Reasonable scheme length
        {
            var scheme = value[..colonIndex].ToLowerInvariant();
            if (DangerousSchemes.Contains(scheme))
            {
                element.RemoveAttribute(attributeName);
            }
        }
    }

    #endregion

    #region Playwright Mode

    private static bool _playwrightInstalled;
    private static readonly SemaphoreSlim _installLock = new(1, 1);

    /// <summary>
    /// Ensure Playwright Chromium browser is installed (one-time setup).
    /// Only called when Playwright mode is enabled.
    /// </summary>
    private static async Task EnsurePlaywrightInstalledAsync()
    {
        if (_playwrightInstalled) return;

        await _installLock.WaitAsync();
        try
        {
            if (_playwrightInstalled) return;

            Console.WriteLine("Installing Playwright Chromium browser (first-time setup)...");
            var exitCode = Microsoft.Playwright.Program.Main(["install", "chromium"]);
            if (exitCode != 0)
                throw new InvalidOperationException($"Failed to install Playwright Chromium browser (exit code {exitCode})");

            _playwrightInstalled = true;
            Console.WriteLine("Playwright Chromium installed successfully.");
        }
        finally
        {
            _installLock.Release();
        }
    }

    /// <summary>
    /// Fetch web content using Playwright headless browser.
    /// Handles JavaScript-rendered pages (SPAs, React apps, etc.)
    /// </summary>
    private async Task<WebFetchResult> FetchWithPlaywrightAsync(Uri uri)
    {
        // Auto-install Chromium on first use
        await EnsurePlaywrightInstalledAsync();

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            ExecutablePath = _config.BrowserExecutablePath // null = use installed Chromium
        });

        var page = await browser.NewPageAsync(new BrowserNewPageOptions
        {
            UserAgent = _config.UserAgent
        });

        // Set timeout from config
        page.SetDefaultTimeout(_config.TimeoutSeconds * 1000);

        try
        {
            Console.WriteLine($"Fetching (Playwright): {uri}");
            
            // Navigate and wait for network idle (JS finished loading)
            var response = await page.GotoAsync(uri.ToString(), new PageGotoOptions
            {
                WaitUntil = WaitUntilState.NetworkIdle,
                Timeout = _config.TimeoutSeconds * 1000
            });

            if (response == null)
                throw new InvalidOperationException("Navigation returned null response");

            // Get final URL after any client-side redirects
            var finalUrl = page.Url;
            var finalUri = new Uri(finalUrl);

            // SSRF protection: validate final URL after any redirects
            await ValidateHostAsync(finalUri);

            // Check response status
            if (!response.Ok)
            {
                throw new HttpRequestException($"HTTP {response.Status}: {response.StatusText}");
            }

            // Get rendered HTML content
            var html = await page.ContentAsync();

            // Validate size
            if (html.Length > MaxHtmlBytes)
            {
                throw new SecurityException($"HTML too large: {html.Length} bytes (max {MaxHtmlBytes})");
            }

            // Get content type from response headers
            var headers = await response.AllHeadersAsync();
            var contentType = headers.TryGetValue("content-type", out var ct) ? ct : "text/html";

            // Sanitize HTML (same as simple mode)
            var sanitized = await SanitizeHtmlAsync(html, finalUri);

            // Save to temp file
            var tempFile = CreateTempFile(".html");
            await File.WriteAllTextAsync(tempFile, sanitized);

            return new WebFetchResult(tempFile, contentType, finalUrl, ".html", isHtmlContent: true);
        }
        catch (TimeoutException ex)
        {
            throw new InvalidOperationException($"Playwright timeout waiting for page to load: {uri}", ex);
        }
        catch (PlaywrightException ex)
        {
            throw new InvalidOperationException($"Playwright error fetching {uri}: {ex.Message}", ex);
        }
    }

    #endregion
}

/// <summary>
/// Security exception for blocked requests
/// </summary>
public class SecurityException : Exception
{
    public SecurityException(string message) : base(message) { }
    public SecurityException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Exception for permanent HTTP failures that should not be retried (403, 404, etc.)
/// </summary>
public class WebFetchPermanentException : Exception
{
    public HttpStatusCode StatusCode { get; }
    
    public WebFetchPermanentException(string message, HttpStatusCode statusCode) : base(message)
    {
        StatusCode = statusCode;
    }
    
    public WebFetchPermanentException(string message, HttpStatusCode statusCode, Exception inner) 
        : base(message, inner)
    {
        StatusCode = statusCode;
    }
}

/// <summary>
/// Result of a web fetch operation. Implements IDisposable for automatic temp file cleanup.
/// Use with 'using' statement to ensure temp files are always cleaned up.
/// </summary>
public sealed class WebFetchResult : IDisposable
{
    private bool _disposed;

    public string TempFilePath { get; }
    public string ContentType { get; }
    public string SourceUrl { get; }
    public string FileExtension { get; }
    public bool IsHtmlContent { get; }

    public WebFetchResult(
        string tempFilePath,
        string contentType,
        string sourceUrl,
        string fileExtension,
        bool isHtmlContent = false)
    {
        TempFilePath = tempFilePath;
        ContentType = contentType;
        SourceUrl = sourceUrl;
        FileExtension = fileExtension;
        IsHtmlContent = isHtmlContent;
    }

    /// <summary>
    /// Clean up the temporary file
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (File.Exists(TempFilePath))
        {
            try
            {
                File.Delete(TempFilePath);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    /// <summary>
    /// Get a friendly description of the content type
    /// </summary>
    public string GetContentDescription()
    {
        return FileExtension switch
        {
            ".pdf" => "PDF Document",
            ".docx" => "Word Document",
            ".xlsx" => "Excel Spreadsheet",
            ".pptx" => "PowerPoint Presentation",
            ".html" => "HTML Page",
            ".md" => "Markdown",
            ".txt" => "Plain Text",
            ".json" => "JSON",
            ".xml" => "XML",
            ".csv" => "CSV Data",
            ".png" or ".jpg" or ".jpeg" or ".tiff" => "Image",
            _ => ContentType
        };
    }
}
