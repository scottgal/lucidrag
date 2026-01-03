using mostlylucid.pagingtaghelper.Models;

namespace Mostlylucid.Shared.Interfaces;

public interface IPagingModel<T> : IPagingModel where T: class
{
 List<T> Data { get; set; }
}