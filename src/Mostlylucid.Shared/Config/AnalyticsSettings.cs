namespace Mostlylucid.Shared.Config;

public class AnalyticsSettings : IConfigSection
{
    public static string Section => "Analytics";
    public string? UmamiPath { get; set; }
    
    public string? WebsiteId { get; set; }
    
    public string? UmamiScript { get; set; }=string.Empty;
}