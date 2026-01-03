using Mostlylucid.Shared.Entities;
using Mostlylucid.Shared.Models;

namespace Mostlylucid.Shared.Mapper;

public static class BlogPostEntityMapper
{
   public static BlogPostDto ToDto(this BlogPostEntity entity, string[] languages = null)
    {
        return new BlogPostDto
        {
            Id = entity.Id.ToString(),
            Title = entity.Title,
            Categories = entity.Categories.Select(x=>x.Name).OrderBy(z=>z).ToArray(),
            Language = entity.LanguageEntity?.Name ?? string.Empty,
            Markdown = entity.Markdown,
            UpdatedDate = entity.UpdatedDate,
            HtmlContent = entity.HtmlContent,
            PlainTextContent = entity.PlainTextContent,
            Slug = entity.Slug,
            WordCount = entity.WordCount,
            PublishedDate = entity.PublishedDate.DateTime,
            Languages = languages ?? Array.Empty<string>(),
            IsPinned = entity.IsPinned,
            IsHidden = entity.IsHidden,
            ScheduledPublishDate = entity.ScheduledPublishDate,
            ShowUpdatedDate = entity.ShowUpdatedDate,
            UpdatedTemplate = entity.UpdatedTemplate
        };
    }
}