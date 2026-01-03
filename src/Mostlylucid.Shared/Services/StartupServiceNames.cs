namespace Mostlylucid.Shared.Services;

/// <summary>
/// Well-known service names for startup coordination.
/// Use these constants when registering/signaling services.
/// </summary>
public static class StartupServiceNames
{
    public const string MarkdownDirectoryWatcher = "MarkdownDirectoryWatcher";
    public const string BlogReconciliation = "BlogReconciliation";
    public const string MarkdownReAddPosts = "MarkdownReAddPosts";
    public const string MarkdownFetchPolling = "MarkdownFetchPolling";
    public const string SemanticIndexing = "SemanticIndexing";
    public const string BrokenLinkChecker = "BrokenLinkChecker";
    public const string ImageDownload = "ImageDownload";
    public const string PopularPostsPolling = "PopularPostsPolling";
}
