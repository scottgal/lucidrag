namespace Mostlylucid.Shared.Models.Email;

public class ConfirmEmailModel : BaseEmailModel
{
    public override string Subject => "Confirm mostlylucid subscription";
    public string ToEmail { get; set; } = string.Empty;
    public string ConfirmUrl { get; set; } = string.Empty;
    public string UnsubscribeUrl { get; set; } = string.Empty;
    public string ManageSubscriptionUrl { get; set; } = string.Empty;
    public SubscriptionType SubscriptionType { get; set; }
}