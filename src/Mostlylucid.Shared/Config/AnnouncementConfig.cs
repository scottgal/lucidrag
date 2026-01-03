namespace Mostlylucid.Shared.Config;

public class AnnouncementConfig : IConfigSection
{
    public static string Section => "Announcement";

    /// <summary>
    /// API token for authentication when updating announcements
    /// Should be a long random string stored in .env / appsettings
    /// </summary>
    public string ApiToken { get; set; } = string.Empty;
}
