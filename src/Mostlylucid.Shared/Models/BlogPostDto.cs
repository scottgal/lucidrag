namespace Mostlylucid.Shared.Models;


public class BlogPostDto
{
    
    public string Id { get; set; }
    public string[] Categories { get; set; } = Array.Empty<string>();
    
    public string Title { get; set; }= string.Empty;
    
    public string Language { get; set; }= string.Empty;
    
    public string Markdown { get; set; }= string.Empty;
    
    public DateTimeOffset? UpdatedDate { get; set; }
    
    public string[] Languages { get; set; } = Array.Empty<string>();
    
    public string HtmlContent { get; set; }= string.Empty;
    
    public string PlainTextContent { get; set; }= string.Empty;
    
    public string Slug { get; set; }= string.Empty;
    
    public int WordCount { get; set; }

    public DateTime PublishedDate { get; set; }

    public bool IsPinned { get; set; }

    public bool IsHidden { get; set; }

    public DateTimeOffset? ScheduledPublishDate { get; set; }

    public bool ShowUpdatedDate { get; set; }

    public string? UpdatedTemplate { get; set; }
}