namespace Mostlylucid.Shared.Config;

public class TranslateServiceConfig :IConfigSection
{
    public static string Section => "TranslateService";
    
    public bool Enabled { get; set; }
    
    public string[] IPs { get; set; }

    public string ServiceIPs
    {
        get => string.Join(";", IPs);
        set
        {
            if(string.IsNullOrEmpty(value)) return;
            if(value.Contains(";"))
            {
                IPs = value.Split(";");
            }
            else
            {
                IPs = new string[]{value};
            }
        }
    }

    public string[] Languages { get; set; }

    public AutoTranslateMode Mode { get; set; } = AutoTranslateMode.SaveToDisk;

    /// <summary>
    /// Maximum characters per batch sent to EasyNMT (default: 1500 to stay well under typical 5000 char limits)
    /// </summary>
    public int MaxBatchCharacters { get; set; } = 1500;

    /// <summary>
    /// Maximum number of sentences per batch (default: 10 for safety)
    /// </summary>
    public int MaxSentencesPerBatch { get; set; } = 10;

    /// <summary>
    /// Force retranslation of all files, ignoring hash-based change detection (default: false)
    /// Useful for development to retranslate everything regardless of whether files have changed
    /// </summary>
    public bool ForceRetranslation { get; set; } = false;
}

public enum AutoTranslateMode
{
   SaveToDisk,
   SaveToDatabase
}