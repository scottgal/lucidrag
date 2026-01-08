using Mostlylucid.DataSummarizer.Configuration;
using Mostlylucid.DataSummarizer.Models;

namespace Mostlylucid.DataSummarizer.Services;

/// <summary>
/// Service for redacting PII values in output based on configuration.
/// By default, all PII is hidden for privacy and security.
/// </summary>
public class PiiRedactionService
{
    private readonly PiiDisplayConfig _config;

    public PiiRedactionService(PiiDisplayConfig? config = null)
    {
        _config = config ?? new PiiDisplayConfig();
    }

    /// <summary>
    /// Redact a value based on its PII type and configuration.
    /// Returns the original value if allowed, otherwise returns redacted version.
    /// </summary>
    public string RedactValue(string value, PiiType piiType)
    {
        // Check if this specific PII type should be shown
        if (ShouldShowPiiType(piiType))
        {
            return value;
        }

        // Redact the value
        return RedactString(value, piiType);
    }

    /// <summary>
    /// Check if a column contains PII and should be redacted
    /// </summary>
    public bool ShouldRedactColumn(ColumnProfile column, List<PiiScanResult>? piiResults = null)
    {
        // If global ShowPiiValues is true, never redact
        if (_config.ShowPiiValues)
        {
            return false;
        }

        // Check if this column was detected as PII
        var piiResult = piiResults?.FirstOrDefault(r => r.ColumnName == column.Name);
        if (piiResult?.IsPii == true && piiResult.PrimaryType.HasValue)
        {
            // Don't redact if this specific type is allowed
            return !ShouldShowPiiType(piiResult.PrimaryType.Value);
        }

        return false;
    }

    /// <summary>
    /// Redact top values from a column profile based on PII detection
    /// </summary>
    public List<ValueCount> RedactTopValues(List<ValueCount> topValues, PiiType? piiType)
    {
        if (_config.ShowPiiValues || piiType == null || ShouldShowPiiType(piiType.Value))
        {
            return topValues;
        }

        return topValues.Select(v => new ValueCount
        {
            Value = RedactString(v.Value ?? "", piiType.Value),
            Count = v.Count,
            Percent = v.Percent
        }).ToList();
    }

    /// <summary>
    /// Check if a specific PII type should be shown based on configuration
    /// </summary>
    private bool ShouldShowPiiType(PiiType piiType)
    {
        // If global override is on, show everything
        if (_config.ShowPiiValues)
        {
            return true;
        }

        // Check per-type settings
        return piiType switch
        {
            PiiType.SSN => _config.TypeSettings.ShowSsn,
            PiiType.CreditCard => _config.TypeSettings.ShowCreditCard,
            PiiType.BankAccount => _config.TypeSettings.ShowBankAccount,
            PiiType.PassportNumber => _config.TypeSettings.ShowPassport,
            PiiType.DriversLicense => _config.TypeSettings.ShowDriversLicense,
            PiiType.Email => _config.TypeSettings.ShowEmail,
            PiiType.PhoneNumber => _config.TypeSettings.ShowPhone,
            PiiType.IPAddress => _config.TypeSettings.ShowIpAddress,
            PiiType.PersonName => _config.TypeSettings.ShowPersonName,
            PiiType.Address => _config.TypeSettings.ShowAddress,
            PiiType.DateOfBirth => _config.TypeSettings.ShowDateOfBirth,
            PiiType.MACAddress => _config.TypeSettings.ShowMacAddress,
            PiiType.URL => _config.TypeSettings.ShowUrl,
            PiiType.UUID => _config.TypeSettings.ShowUuid,
            PiiType.USState => _config.TypeSettings.ShowUsState,
            PiiType.ZipCode => _config.TypeSettings.ShowZipCode,
            PiiType.VIN => _config.TypeSettings.ShowVin,
            PiiType.IBAN => _config.TypeSettings.ShowIban,
            PiiType.RoutingNumber => _config.TypeSettings.ShowRoutingNumber,
            PiiType.Other => _config.TypeSettings.ShowOther,
            _ => false
        };
    }

    /// <summary>
    /// Redact a string value based on PII type
    /// </summary>
    private string RedactString(string value, PiiType piiType)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var label = _config.ShowPiiTypeLabel ? GetPiiTypeLabel(piiType) : "";
        
        // For short values, just use the label
        if (value.Length <= _config.VisibleChars * 2)
        {
            return label.Length > 0 ? label : new string(_config.RedactionChar[0], value.Length);
        }

        // Show first/last N characters with redaction in middle
        var visChars = Math.Min(_config.VisibleChars, value.Length / 2);
        var start = value.Substring(0, visChars);
        var end = value.Substring(value.Length - visChars);
        var middleLength = Math.Max(3, Math.Min(10, value.Length - (visChars * 2)));
        var middle = new string(_config.RedactionChar[0], middleLength);

        // For emails, preserve @ and domain structure
        if (piiType == PiiType.Email && value.Contains('@'))
        {
            var parts = value.Split('@');
            if (parts.Length == 2)
            {
                var localPart = parts[0].Length > visChars ? parts[0].Substring(0, visChars) + middle : middle;
                var domainParts = parts[1].Split('.');
                var domain = domainParts.Length > 1
                    ? middle + "." + domainParts[^1]
                    : middle;
                return $"{localPart}@{domain}";
            }
        }

        // For SSN, show format
        if (piiType == PiiType.SSN && value.Contains('-'))
        {
            return $"***-**-{value.Substring(value.Length - 4)}";
        }

        // For credit cards, show last 4
        if (piiType == PiiType.CreditCard)
        {
            var last4 = value.Length >= 4 ? value.Substring(value.Length - 4) : value;
            return $"**** **** **** {last4}";
        }

        // For phone numbers, show format
        if (piiType == PiiType.PhoneNumber)
        {
            return "***-***-" + (value.Length >= 4 ? value.Substring(value.Length - 4) : "****");
        }

        // Default: start + middle + end
        var result = $"{start}{middle}{end}";
        
        // Add label if configured
        if (label.Length > 0)
        {
            result = $"{label} {result}";
        }

        return result;
    }

    /// <summary>
    /// Get a human-readable label for a PII type
    /// Use <> instead of [] to avoid Spectre.Console markup conflicts
    /// </summary>
    private static string GetPiiTypeLabel(PiiType piiType)
    {
        return piiType switch
        {
            PiiType.SSN => "<SSN>",
            PiiType.CreditCard => "<CARD>",
            PiiType.BankAccount => "<ACCT>",
            PiiType.PassportNumber => "<PASSPORT>",
            PiiType.DriversLicense => "<DL>",
            PiiType.Email => "<EMAIL>",
            PiiType.PhoneNumber => "<PHONE>",
            PiiType.IPAddress => "<IP>",
            PiiType.PersonName => "<NAME>",
            PiiType.Address => "<ADDR>",
            PiiType.DateOfBirth => "<DOB>",
            PiiType.Other => "<PII>",
            _ => ""
        };
    }
}
