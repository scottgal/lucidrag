namespace Mostlylucid.Shared.Config;

public class NewsletterConfig : IConfigSection
{
    public static string Section => "Newsletter";
    
    public string SchedulerServiceUrl { get; set; } = string.Empty;
    public string AppHostUrl { get; set; } = string.Empty;
}