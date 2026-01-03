namespace Mostlylucid.Shared.Models;

public record BlogPostQueryModel(string Slug, string Language);

public record PostListQueryModel(
    string? Language = Constants.EnglishLanguage,
    int? Page = null,
    int? PageSize = null,
    DateTime? StartDate = null,
    DateTime? EndDate = null,
    string[]? Categories = null,
    string? orderBy = null,
    string? orderDir = null);