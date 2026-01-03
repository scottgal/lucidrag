namespace Mostlylucid.Shared.Config;

public class SmtpSettings : IConfigSection
{
    public static string Section => "SmtpSettings";
    public string Server { get; set; }
    public int Port { get; set; }
    public string SenderName { get; set; }
    public string Username { get; set; }
    public string SenderEmail { get; set; }
    public string Password { get; set; }
    public int EmailSendTry { get; set; }
    public bool EmailSendFailed { get; set; }
    public bool EnableSSL { get; set; }
    
    public string ToMail  { get; set; }
}