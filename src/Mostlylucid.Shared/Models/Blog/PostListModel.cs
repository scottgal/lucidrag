namespace Mostlylucid.Shared.Models.Blog;

public class PostListModel
{

    public string Id { get; set; }
    public string Title { get; set; }= string.Empty;
    public string Slug { get; set; }= string.Empty;
    public DateTime PublishedDate { get; set; }

    public DateTime? UpdatedDate { get; set; }

    public int Views { get; set; }
    public string Language { get; set; }= string.Empty;

    public string[] Languages { get; set; } = Array.Empty<string>();

    public string[] Categories { get; set; } = Array.Empty<string>();

    public string ReadingTime
    {
        get
        {
            var readCount = (float)WordCount / 200;
            return readCount <1  ? "Less than a minute" : $"{Math.Round(readCount)} minute read";
        }
    }

    public int WordCount { get; set; }

    public string Summary { get; set; }= string.Empty;
}
