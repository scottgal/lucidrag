namespace Mostlylucid.DataSummarizer.Configuration;

/// <summary>
/// Configuration for PII display behavior in output.
/// By default, PII values are hidden for privacy and security.
/// </summary>
public class PiiDisplayConfig
{
    /// <summary>
    /// Show actual PII values in output (default: false for security)
    /// </summary>
    public bool ShowPiiValues { get; set; } = false;

    /// <summary>
    /// Show specific PII types (overrides ShowPiiValues per type)
    /// </summary>
    public PiiTypeDisplaySettings TypeSettings { get; set; } = new();
    
    /// <summary>
    /// Redaction character to use for hidden values (default: *)
    /// </summary>
    public string RedactionChar { get; set; } = "*";
    
    /// <summary>
    /// Number of visible characters at start/end when redacting (default: 2)
    /// Example: "john.doe@example.com" -> "jo***@***om"
    /// </summary>
    public int VisibleChars { get; set; } = 2;
    
    /// <summary>
    /// Show PII type labels when redacting (e.g., "[EMAIL_REDACTED]")
    /// </summary>
    public bool ShowPiiTypeLabel { get; set; } = true;
}

/// <summary>
/// Per-type PII display settings
/// </summary>
public class PiiTypeDisplaySettings
{
    /// <summary>
    /// Show SSN values (default: false - CRITICAL PII)
    /// </summary>
    public bool ShowSsn { get; set; } = false;

    /// <summary>
    /// Show credit card numbers (default: false - CRITICAL PII)
    /// </summary>
    public bool ShowCreditCard { get; set; } = false;

    /// <summary>
    /// Show bank account numbers (default: false - CRITICAL PII)
    /// </summary>
    public bool ShowBankAccount { get; set; } = false;

    /// <summary>
    /// Show passport numbers (default: false - HIGH PII)
    /// </summary>
    public bool ShowPassport { get; set; } = false;

    /// <summary>
    /// Show driver's license numbers (default: false - HIGH PII)
    /// </summary>
    public bool ShowDriversLicense { get; set; } = false;

    /// <summary>
    /// Show email addresses (default: false)
    /// </summary>
    public bool ShowEmail { get; set; } = false;

    /// <summary>
    /// Show phone numbers (default: false)
    /// </summary>
    public bool ShowPhone { get; set; } = false;

    /// <summary>
    /// Show IP addresses (default: false)
    /// </summary>
    public bool ShowIpAddress { get; set; } = false;

    /// <summary>
    /// Show person names (default: false)
    /// </summary>
    public bool ShowPersonName { get; set; } = false;

    /// <summary>
    /// Show addresses (default: false)
    /// </summary>
    public bool ShowAddress { get; set; } = false;

    /// <summary>
    /// Show dates of birth (default: false)
    /// </summary>
    public bool ShowDateOfBirth { get; set; } = false;

    /// <summary>
    /// Show MAC addresses (default: true - lower risk)
    /// </summary>
    public bool ShowMacAddress { get; set; } = true;

    /// <summary>
    /// Show URLs (default: true - lower risk)
    /// </summary>
    public bool ShowUrl { get; set; } = true;

    /// <summary>
    /// Show UUIDs (default: true - not PII)
    /// </summary>
    public bool ShowUuid { get; set; } = true;

    /// <summary>
    /// Show US state codes (default: true - not PII)
    /// </summary>
    public bool ShowUsState { get; set; } = true;

    /// <summary>
    /// Show ZIP codes (default: true - quasi-identifier, low risk)
    /// </summary>
    public bool ShowZipCode { get; set; } = true;

    /// <summary>
    /// Show VINs (default: false)
    /// </summary>
    public bool ShowVin { get; set; } = false;

    /// <summary>
    /// Show IBANs (default: false)
    /// </summary>
    public bool ShowIban { get; set; } = false;

    /// <summary>
    /// Show routing numbers (default: false)
    /// </summary>
    public bool ShowRoutingNumber { get; set; } = false;

    /// <summary>
    /// Show other PII types (default: false)
    /// </summary>
    public bool ShowOther { get; set; } = false;
}
