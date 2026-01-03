using mostlylucid.pagingtaghelper.Models;

namespace Mostlylucid.Shared.Models;

public class BasePagingModel<T> : Interfaces.IPagingModel<T> where T : class
{
    public int Page { get; set; }
    public int TotalItems { get; set; } = 0;
    public int PageSize { get; set; }
    public ViewType ViewType { get; set; } = ViewType.TailwindAndDaisy;
    public string LinkUrl { get; set; }
    public List<T> Data { get; set; }
}