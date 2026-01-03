using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using LucidRAG.Config;

namespace LucidRAG.Filters;

/// <summary>
/// Attribute that blocks write operations (POST, PUT, DELETE) when demo mode is enabled.
/// Apply to controllers or actions that should be read-only in demo mode.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public class DemoModeWriteBlockAttribute : Attribute, IFilterFactory
{
    /// <summary>
    /// Custom message for the operation being blocked (e.g., "Document upload", "Collection creation")
    /// </summary>
    public string? Operation { get; set; }

    public bool IsReusable => true;

    public IFilterMetadata CreateInstance(IServiceProvider serviceProvider)
    {
        var config = serviceProvider.GetRequiredService<IOptions<RagDocumentsConfig>>();
        return new DemoModeWriteBlockFilter(config.Value, Operation);
    }
}

/// <summary>
/// Filter that returns 403 Forbidden for write operations when demo mode is enabled.
/// </summary>
public class DemoModeWriteBlockFilter(RagDocumentsConfig config, string? operation) : IActionFilter
{
    private static readonly HashSet<string> WriteHttpMethods = ["POST", "PUT", "PATCH", "DELETE"];

    public void OnActionExecuting(ActionExecutingContext context)
    {
        if (!config.DemoMode.Enabled)
        {
            return;
        }

        var httpMethod = context.HttpContext.Request.Method.ToUpperInvariant();

        if (!WriteHttpMethods.Contains(httpMethod))
        {
            return;
        }

        var operationName = operation ?? $"{httpMethod} operations";

        context.Result = new ObjectResult(new
        {
            error = $"{operationName} disabled in demo mode",
            message = config.DemoMode.BannerMessage,
            demoMode = true
        })
        {
            StatusCode = StatusCodes.Status403Forbidden
        };
    }

    public void OnActionExecuted(ActionExecutedContext context)
    {
        // No post-action processing needed
    }
}
