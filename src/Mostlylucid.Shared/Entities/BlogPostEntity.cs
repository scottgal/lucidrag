using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using NpgsqlTypes;

namespace Mostlylucid.Shared.Entities;


public class BlogPostEntity
{
    [Key]

    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }
    
    public string Title { get; set; }

    public string Slug { get; set; }

    public DateTimeOffset? UpdatedDate { get; set; }

    public string Markdown { get; set; } = string.Empty;
    
 
    public string HtmlContent { get; set; }
    

    public string PlainTextContent { get; set; }
    
  
    public string ContentHash { get; set; }
    

    public int WordCount { get; set; }
    
    public int LanguageId { get; set; }
    
    
    public LanguageEntity LanguageEntity { get; set; }
    
    
    public ICollection<CommentEntity> Comments { get; set; }
    public ICollection<CategoryEntity> Categories { get; set; }
    

    public DateTimeOffset PublishedDate { get; set; }

    public bool IsPinned { get; set; }

    public bool IsHidden { get; set; }

    public DateTimeOffset? ScheduledPublishDate { get; set; }

    public bool ShowUpdatedDate { get; set; }

    public string? UpdatedTemplate { get; set; }

    /// <summary>
    /// Sentiment analysis metadata stored as JSON
    /// Contains sentiment score, emotional tones, formality, subjectivity, etc.
    /// </summary>
    public string? SentimentMetadata { get; set; }

    [DatabaseGenerated(DatabaseGeneratedOption.Computed)]

    public NpgsqlTsVector SearchVector { get; set; }


}