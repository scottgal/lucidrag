namespace Mostlylucid.Shared.Config.Markdown;

public class MarkdownConfig : IConfigSection
{
    public static string Section => "Markdown";
    public string MarkdownPath { get; set; }= "Markdown";
    public string MarkdownTranslatedPath { get; set; } = "Markdown/translated";
    public string MarkdownCommentsPath { get; set; }= "Markdown/comments";

    public string MarkdownNotModeratedCommentsPath { get; set; }= "Markdown/notmoderatedcomments";

    /// <summary>
    /// When true, re-processes and saves ALL markdown posts to the database on startup.
    /// Useful for testing or after schema changes. Should be false in production.
    /// </summary>
    public bool ReAddPosts { get; set; } = false;
}