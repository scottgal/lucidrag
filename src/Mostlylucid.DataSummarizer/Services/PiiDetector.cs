using System.Text.RegularExpressions;
using Mostlylucid.DataSummarizer.Configuration;
using Mostlylucid.DataSummarizer.Models;
using Mostlylucid.DataSummarizer.Services.Onnx;

namespace Mostlylucid.DataSummarizer.Services;

/// <summary>
/// Detects Personally Identifiable Information (PII) and sensitive data in columns.
/// Uses source-generated regex patterns for common PII types, optionally enhanced
/// with ONNX-based semantic classification for subtle cases.
/// </summary>
public partial class PiiDetector : IAsyncDisposable
{
    private readonly bool _verbose;
    private TinyClassifier? _classifier;
    private bool _classifierEnabled;

    public PiiDetector(bool verbose = false)
    {
        _verbose = verbose;
    }
    
    /// <summary>
    /// Enable ONNX-based classification for enhanced PII detection.
    /// This adds semantic understanding to complement regex patterns.
    /// </summary>
    public async Task EnableClassifierAsync(OnnxConfig? config = null, CancellationToken ct = default)
    {
        _classifier = new TinyClassifier(_verbose, config);
        await _classifier.InitializeAsync(ct);
        _classifierEnabled = true;
        if (_verbose) Console.WriteLine("[PiiDetector] Classifier enabled for ensemble detection");
    }
    
    public async ValueTask DisposeAsync()
    {
        _classifier?.Dispose();
        await Task.CompletedTask;
    }

    #region Source-Generated Regex Patterns

    // SSN (US) - XXX-XX-XXXX or XXXXXXXXX
    [GeneratedRegex(@"^\d{3}-?\d{2}-?\d{4}$", RegexOptions.Compiled)]
    private static partial Regex SsnRegex();

    // Credit Card (major card patterns)
    [GeneratedRegex(@"^(?:4[0-9]{12}(?:[0-9]{3})?|5[1-5][0-9]{14}|3[47][0-9]{13}|6(?:011|5[0-9]{2})[0-9]{12})$", RegexOptions.Compiled)]
    private static partial Regex CreditCardRegex();

    // Credit Card (formatted with spaces/dashes)
    [GeneratedRegex(@"^\d{4}[\s-]?\d{4}[\s-]?\d{4}[\s-]?\d{4}$", RegexOptions.Compiled)]
    private static partial Regex CreditCardFormattedRegex();

    // Email
    [GeneratedRegex(@"^[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}$", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex EmailRegex();

    // US Phone Number
    [GeneratedRegex(@"^(\+1)?[\s.-]?\(?\d{3}\)?[\s.-]?\d{3}[\s.-]?\d{4}$", RegexOptions.Compiled)]
    private static partial Regex UsPhoneRegex();

    // International Phone
    [GeneratedRegex(@"^\+?[1-9]\d{9,14}$", RegexOptions.Compiled)]
    private static partial Regex IntlPhoneRegex();

    // IPv4 Address
    [GeneratedRegex(@"^(?:(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.){3}(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)$", RegexOptions.Compiled)]
    private static partial Regex Ipv4Regex();

    // IPv6 Address
    [GeneratedRegex(@"^([0-9a-fA-F]{1,4}:){7}[0-9a-fA-F]{1,4}$", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex Ipv6Regex();

    // MAC Address
    [GeneratedRegex(@"^([0-9A-Fa-f]{2}[:-]){5}([0-9A-Fa-f]{2})$", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex MacAddressRegex();

    // US Zip Code
    [GeneratedRegex(@"^\d{5}(-\d{4})?$", RegexOptions.Compiled)]
    private static partial Regex ZipCodeRegex();

    // US State (2-letter)
    [GeneratedRegex(@"^(AL|AK|AZ|AR|CA|CO|CT|DE|FL|GA|HI|ID|IL|IN|IA|KS|KY|LA|ME|MD|MA|MI|MN|MS|MO|MT|NE|NV|NH|NJ|NM|NY|NC|ND|OH|OK|OR|PA|RI|SC|SD|TN|TX|UT|VT|VA|WA|WV|WI|WY|DC)$", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex UsStateRegex();

    // Date MM/DD/YYYY
    [GeneratedRegex(@"^(0[1-9]|1[0-2])/(0[1-9]|[12]\d|3[01])/(19|20)\d{2}$", RegexOptions.Compiled)]
    private static partial Regex DateMdyRegex();

    // Date YYYY-MM-DD
    [GeneratedRegex(@"^(19|20)\d{2}-(0[1-9]|1[0-2])-(0[1-9]|[12]\d|3[01])$", RegexOptions.Compiled)]
    private static partial Regex DateYmdRegex();

    // UUID
    [GeneratedRegex(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex UuidRegex();

    // URL
    [GeneratedRegex(@"^https?://[^\s/$.?#].[^\s]*$", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    // Bank Account (generic - 8-17 digits)
    [GeneratedRegex(@"^\d{8,17}$", RegexOptions.Compiled)]
    private static partial Regex BankAccountRegex();

    // Routing Number (US - 9 digits)
    [GeneratedRegex(@"^[0-9]{9}$", RegexOptions.Compiled)]
    private static partial Regex RoutingNumberRegex();

    // Passport (alphanumeric 6-9 chars, must contain at least one digit to avoid matching plain words)
    [GeneratedRegex(@"^(?=.*[0-9])[A-Z0-9]{6,9}$", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex PassportRegex();

    // VIN (Vehicle Identification Number)
    [GeneratedRegex(@"^[A-HJ-NPR-Z0-9]{17}$", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex VinRegex();

    // IBAN (International Bank Account Number)
    [GeneratedRegex(@"^[A-Z]{2}\d{2}[A-Z0-9]{4,30}$", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex IbanRegex();

    #endregion

    /// <summary>
    /// All PII patterns with their associated types, ordered by specificity
    /// </summary>
    private static readonly List<(PiiType Type, Func<Regex> GetRegex, string Description)> Patterns =
    [
        (PiiType.SSN, SsnRegex, "US Social Security Number"),
        (PiiType.CreditCard, CreditCardRegex, "Credit Card Number"),
        (PiiType.CreditCard, CreditCardFormattedRegex, "Credit Card (formatted)"),
        (PiiType.Email, EmailRegex, "Email Address"),
        (PiiType.PhoneNumber, UsPhoneRegex, "US Phone Number"),
        (PiiType.IPAddress, Ipv4Regex, "IPv4 Address"),
        (PiiType.IPAddress, Ipv6Regex, "IPv6 Address"),
        (PiiType.MACAddress, MacAddressRegex, "MAC Address"),
        (PiiType.UUID, UuidRegex, "UUID"),
        (PiiType.URL, UrlRegex, "URL"),
        (PiiType.DateOfBirth, DateMdyRegex, "Date (MM/DD/YYYY)"),
        (PiiType.DateOfBirth, DateYmdRegex, "Date (YYYY-MM-DD)"),
        (PiiType.USState, UsStateRegex, "US State Code"),
        (PiiType.ZipCode, ZipCodeRegex, "US Zip Code"),
        (PiiType.VIN, VinRegex, "Vehicle Identification Number"),
        (PiiType.IBAN, IbanRegex, "IBAN"),
        (PiiType.RoutingNumber, RoutingNumberRegex, "US Routing Number"),
        (PiiType.PhoneNumber, IntlPhoneRegex, "International Phone"),
        (PiiType.PassportNumber, PassportRegex, "Passport Number"),
        (PiiType.BankAccount, BankAccountRegex, "Bank Account Number"),
    ];

    /// <summary>
    /// Scan a column's sample values for PII
    /// </summary>
    public PiiScanResult ScanColumn(string columnName, IEnumerable<object?> sampleValues, int totalRows)
    {
        var result = new PiiScanResult
        {
            ColumnName = columnName,
            DetectedTypes = [],
            Confidence = 0,
            SampleMatches = []
        };

        var values = sampleValues
            .Where(v => v != null)
            .Select(v => v!.ToString() ?? "")
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Take(1000) // Sample for performance
            .ToList();

        // Even without samples, we should check column name for PII hints
        // So don't return early here - continue to name-based detection

        var detections = new Dictionary<PiiType, int>();
        var sampleMatches = new Dictionary<PiiType, List<string>>();

        // Only scan values if we have any
        if (values.Count > 0)
        {
            foreach (var value in values)
            {
                foreach (var (type, getRegex, _) in Patterns)
                {
                    if (getRegex().IsMatch(value))
                    {
                        detections.TryAdd(type, 0);
                        detections[type]++;

                        sampleMatches.TryAdd(type, []);
                        if (sampleMatches[type].Count < 3)
                        {
                            // Redact the sample for safety
                            sampleMatches[type].Add(RedactValue(value, type));
                        }
                        break; // One match per value is enough
                    }
                }
            }

            if (detections.Count > 0)
            {
                // Find the most common PII type
                var topDetection = detections.OrderByDescending(d => d.Value).First();
                var matchRate = (double)topDetection.Value / values.Count;

                result.DetectedTypes = detections
                    .Where(d => (double)d.Value / values.Count > 0.1) // At least 10% match rate
                    .Select(d => new PiiDetection
                    {
                        Type = d.Key,
                        MatchCount = d.Value,
                        MatchRate = (double)d.Value / values.Count,
                        Samples = sampleMatches.GetValueOrDefault(d.Key, [])
                    })
                    .OrderByDescending(d => d.MatchRate)
                    .ToList();

                if (result.DetectedTypes.Count > 0)
                {
                    result.Confidence = result.DetectedTypes.First().MatchRate;
                    result.IsPii = result.Confidence > 0.5;
                    result.PrimaryType = result.DetectedTypes.First().Type;
                    result.RiskLevel = ClassifyRisk(result.PrimaryType.Value, result.Confidence);
                }
            }
        }

        // Also check column name for PII hints
        var nameHint = DetectFromColumnName(columnName);
        if (nameHint != null && result.PrimaryType == null)
        {
            result.PrimaryType = nameHint;
            result.Confidence = 0.3; // Lower confidence for name-only detection
            result.IsPii = true;
            result.RiskLevel = PiiRiskLevel.Medium;
            result.DetectedTypes.Add(new PiiDetection
            {
                Type = nameHint.Value,
                MatchCount = 0,
                MatchRate = 0,
                Samples = [],
                DetectedFromName = true
            });
        }

        return result;
    }
    
    /// <summary>
    /// Scan a column using ensemble detection (regex + classifier + uniqueness).
    /// Returns a comprehensive PII risk assessment.
    /// </summary>
    public async Task<ColumnPiiRisk> ScanColumnEnsembleAsync(
        ColumnProfile column,
        CancellationToken ct = default)
    {
        var risk = new ColumnPiiRisk();
        
        // 1. Regex-based detection (fast, reliable for structured patterns)
        var regexResult = ScanColumn(column.Name, 
            column.TopValues?.Select(tv => (object?)tv.Value) ?? [], 
            (int)column.Count);
        
        if (regexResult.DetectedTypes.Count > 0)
        {
            risk.Regex = new RegexDetection
            {
                HitRate = regexResult.Confidence,
                HitCount = regexResult.DetectedTypes.Sum(d => d.MatchCount),
                Types = regexResult.DetectedTypes.Select(d => d.Type).ToList(),
                TypeCounts = regexResult.DetectedTypes.ToDictionary(d => d.Type, d => d.MatchCount)
            };
            risk.DetectedTypes = regexResult.DetectedTypes.Select(d => d.Type).ToList();
            risk.Reasons.Add($"Regex matched {regexResult.PrimaryType} with {regexResult.Confidence:P0} confidence");
        }
        
        // 2. Classifier-based detection (semantic understanding for subtle cases)
        if (_classifierEnabled && _classifier != null)
        {
            var sampleValues = column.TopValues?
                .Take(5)
                .Select(tv => tv.Value)
                .ToList();
                
            var (label, confidence) = await _classifier.ClassifyPiiAsync(
                column.Name, sampleValues, ct);
            
            if (label != "not_pii" && confidence > 0.5)
            {
                var classifierType = MapLabelToPiiType(label);
                risk.Classifier = new ClassifierDetection
                {
                    HitRate = confidence,
                    Types = classifierType != PiiType.None ? [classifierType] : [],
                    AverageConfidence = confidence,
                    HighConfidenceRate = confidence > 0.8 ? 1.0 : 0.0,
                    AgreementRate = risk.Regex != null && 
                        risk.Regex.Types.Contains(classifierType) ? 1.0 : 0.0
                };
                
                if (!risk.DetectedTypes.Contains(classifierType) && classifierType != PiiType.None)
                {
                    risk.DetectedTypes.Add(classifierType);
                }
                risk.Reasons.Add($"Classifier detected {label} with {confidence:P0} confidence");
            }
        }
        
        // 3. Identifier/uniqueness risk (quasi-identifiers)
        var cardinalityRatio = column.Count > 0 ? (double)column.UniqueCount / column.Count : 0;
        if (cardinalityRatio > 0.9 && column.InferredType == ColumnType.Text)
        {
            // High uniqueness text column - potential identifier
            risk.UniqueIdentifierRisk = new IdentifierRisk
            {
                Level = cardinalityRatio > 0.99 ? PiiRiskLevel.High : PiiRiskLevel.Medium,
                CardinalityRatio = cardinalityRatio,
                HasFixedLength = HasFixedLength(column),
                AppearsSequential = AppearsSequential(column),
                Reason = $"High uniqueness ({cardinalityRatio:P0}) in text column"
            };
            risk.Reasons.Add($"High uniqueness ratio ({cardinalityRatio:P1}) suggests identifier");
        }
        
        // Determine overall risk level
        risk.RiskLevel = DetermineOverallRisk(risk);
        risk.RecommendedAction = GetRecommendedAction(risk);
        
        return risk;
    }
    
    private static PiiType MapLabelToPiiType(string label) => label switch
    {
        "email" => PiiType.Email,
        "phone" => PiiType.PhoneNumber,
        "name" => PiiType.PersonName,
        "address" => PiiType.Address,
        "ssn" => PiiType.SSN,
        "credit_card" => PiiType.CreditCard,
        "ip_address" => PiiType.IPAddress,
        "password" => PiiType.Other,
        "dob" => PiiType.DateOfBirth,
        _ => PiiType.None
    };
    
    private static bool HasFixedLength(ColumnProfile column)
    {
        // Check if all top values have the same length (suggests structured ID)
        var lengths = column.TopValues?
            .Select(tv => tv.Value?.Length ?? 0)
            .Distinct()
            .Take(2)
            .ToList();
        return lengths?.Count == 1;
    }
    
    private static bool AppearsSequential(ColumnProfile column)
    {
        // Simple heuristic: if values contain sequential numeric patterns
        var values = column.TopValues?.Select(tv => tv.Value).Take(5).ToList();
        if (values == null || values.Count < 2) return false;
        
        // Extract numeric portions and check if they're sequential
        var numbers = values
            .Select(v => v?.All(c => char.IsDigit(c) || c == '-') == true)
            .ToList();
        return numbers.Count(n => n) >= values.Count / 2;
    }
    
    private static PiiRiskLevel DetermineOverallRisk(ColumnPiiRisk risk)
    {
        // Ensemble decision: combine signals
        var signals = new List<(PiiRiskLevel Level, double Weight)>();
        
        if (risk.Regex != null)
        {
            var regexRisk = risk.Regex.Types.Any(t => 
                t is PiiType.SSN or PiiType.CreditCard or PiiType.BankAccount) 
                ? PiiRiskLevel.Critical 
                : risk.Regex.HitRate > 0.7 ? PiiRiskLevel.High : PiiRiskLevel.Medium;
            signals.Add((regexRisk, 0.5));
        }
        
        if (risk.Classifier != null)
        {
            var classifierRisk = risk.Classifier.AverageConfidence > 0.8 
                ? PiiRiskLevel.High 
                : PiiRiskLevel.Medium;
            signals.Add((classifierRisk, 0.3));
        }
        
        if (risk.UniqueIdentifierRisk != null)
        {
            signals.Add((risk.UniqueIdentifierRisk.Level, 0.2));
        }
        
        if (signals.Count == 0) return PiiRiskLevel.None;
        
        // Weighted max (bias towards highest risk)
        var maxRisk = signals.Max(s => s.Level);
        return maxRisk;
    }
    
    private static string GetRecommendedAction(ColumnPiiRisk risk) => risk.RiskLevel switch
    {
        PiiRiskLevel.Critical => "EXCLUDE from output or use heavy masking (e.g., hash)",
        PiiRiskLevel.High => "Mask or redact values in synthetic output",
        PiiRiskLevel.Medium => "Consider using Faker patterns for realistic pseudonymization",
        PiiRiskLevel.Low => "Safe for synthetic generation with distribution matching",
        _ => "No action needed"
    };
    
    /// <summary>
    /// Scan all columns in a profile for PII using ensemble detection.
    /// </summary>
    public async Task<List<(ColumnProfile Column, ColumnPiiRisk Risk)>> ScanProfileEnsembleAsync(
        DataProfile profile,
        CancellationToken ct = default)
    {
        var results = new List<(ColumnProfile, ColumnPiiRisk)>();
        
        foreach (var col in profile.Columns)
        {
            ct.ThrowIfCancellationRequested();
            var risk = await ScanColumnEnsembleAsync(col, ct);
            if (risk.RiskLevel != PiiRiskLevel.None)
            {
                results.Add((col, risk));
            }
        }
        
        return results;
    }

    /// <summary>
    /// Scan all columns in a profile for PII
    /// </summary>
    public List<PiiScanResult> ScanProfile(DataProfile profile)
    {
        var results = new List<PiiScanResult>();

        foreach (var col in profile.Columns)
        {
            // Get sample values from top values if available
            var samples = col.TopValues?.Select(tv => (object?)tv.Value).ToList() 
                ?? new List<object?>();

            var result = ScanColumn(col.Name, samples, (int)profile.RowCount);
            if (result.IsPii || result.DetectedTypes.Count > 0)
            {
                results.Add(result);
            }
        }

        return results;
    }

    /// <summary>
    /// Generate alerts for detected PII
    /// </summary>
    public List<DataAlert> GeneratePiiAlerts(List<PiiScanResult> piiResults)
    {
        var alerts = new List<DataAlert>();

        foreach (var result in piiResults.Where(r => r.IsPii))
        {
            var severity = result.RiskLevel switch
            {
                PiiRiskLevel.Critical => AlertSeverity.Error,
                PiiRiskLevel.High => AlertSeverity.Error,
                PiiRiskLevel.Medium => AlertSeverity.Warning,
                _ => AlertSeverity.Info
            };

            alerts.Add(new DataAlert
            {
                Severity = severity,
                Column = result.ColumnName,
                Type = AlertType.PiiDetected,
                Message = $"Potential {result.PrimaryType} detected ({result.Confidence:P0} confidence). " +
                         $"Risk level: {result.RiskLevel}. Consider masking or excluding this column."
            });
        }

        return alerts;
    }

    private static PiiType? DetectFromColumnName(string columnName)
    {
        var lower = columnName.ToLowerInvariant();

        // SSN patterns
        if (lower.Contains("ssn") || lower.Contains("social_security") || lower.Contains("socialsecurity"))
            return PiiType.SSN;

        // Credit card
        if (lower.Contains("credit") && (lower.Contains("card") || lower.Contains("number")))
            return PiiType.CreditCard;
        if (lower.Contains("card_number") || lower.Contains("cardnumber") || lower.Contains("cc_num"))
            return PiiType.CreditCard;

        // Email
        if (lower.Contains("email") || lower.Contains("e_mail") || lower.Contains("e-mail"))
            return PiiType.Email;

        // Phone
        if (lower.Contains("phone") || lower.Contains("mobile") || lower.Contains("cell") || lower.Contains("tel"))
            return PiiType.PhoneNumber;

        // Address
        if (lower.Contains("address") || lower.Contains("street") || lower.Contains("addr"))
            return PiiType.Address;

        // Name
        if (lower == "name" || lower.Contains("first_name") || lower.Contains("last_name") || 
            lower.Contains("firstname") || lower.Contains("lastname") || lower.Contains("full_name"))
            return PiiType.PersonName;

        // IP
        if (lower.Contains("ip_address") || lower.Contains("ipaddress") || lower == "ip")
            return PiiType.IPAddress;

        // Date of birth
        if (lower.Contains("dob") || lower.Contains("birth") || lower.Contains("birthday"))
            return PiiType.DateOfBirth;

        // Passport
        if (lower.Contains("passport"))
            return PiiType.PassportNumber;

        // Driver's license
        if (lower.Contains("license") || lower.Contains("licence") || lower.Contains("dl_"))
            return PiiType.DriversLicense;

        return null;
    }

    private static PiiRiskLevel ClassifyRisk(PiiType type, double confidence)
    {
        // High-risk PII types
        if (type is PiiType.SSN or PiiType.CreditCard or PiiType.BankAccount or PiiType.PassportNumber)
            return confidence > 0.7 ? PiiRiskLevel.Critical : PiiRiskLevel.High;

        // Medium-risk PII
        if (type is PiiType.Email or PiiType.PhoneNumber or PiiType.DriversLicense or PiiType.DateOfBirth)
            return confidence > 0.7 ? PiiRiskLevel.High : PiiRiskLevel.Medium;

        // Lower-risk PII
        return confidence > 0.7 ? PiiRiskLevel.Medium : PiiRiskLevel.Low;
    }

    private static string RedactValue(string value, PiiType type)
    {
        // Redact sensitive data for display
        if (value.Length <= 4) return "****";
        
        return type switch
        {
            PiiType.SSN => "***-**-" + value[^4..],
            PiiType.CreditCard => "**** **** **** " + value[^4..],
            PiiType.Email => value[..2] + "***@***" + (value.Contains('@') ? value[(value.LastIndexOf('.')..)] : ""),
            PiiType.PhoneNumber => "***-***-" + value[^4..],
            _ => value[..2] + new string('*', Math.Min(value.Length - 4, 10)) + value[^2..]
        };
    }
}

/// <summary>
/// Result of PII scanning for a column
/// </summary>
public class PiiScanResult
{
    public string ColumnName { get; set; } = "";
    public bool IsPii { get; set; }
    public PiiType? PrimaryType { get; set; }
    public double Confidence { get; set; }
    public PiiRiskLevel RiskLevel { get; set; }
    public List<PiiDetection> DetectedTypes { get; set; } = [];
    public List<string> SampleMatches { get; set; } = [];
}

/// <summary>
/// A specific PII detection
/// </summary>
public class PiiDetection
{
    public PiiType Type { get; set; }
    public int MatchCount { get; set; }
    public double MatchRate { get; set; }
    public List<string> Samples { get; set; } = [];
    public bool DetectedFromName { get; set; }
}

// PiiType and PiiRiskLevel enums are defined in Models/DataProfile.cs
