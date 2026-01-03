using Mostlylucid.Shared.Models.Email;

namespace Mostlylucid.Shared.Models.EmailSubscription;


public class EmailTemplateModel : EmailRenderingModel
{
    public string ToEmail { get; set; } = string.Empty;
}