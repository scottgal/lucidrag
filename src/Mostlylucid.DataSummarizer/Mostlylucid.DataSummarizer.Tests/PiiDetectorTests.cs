using Mostlylucid.DataSummarizer.Models;
using Mostlylucid.DataSummarizer.Services;

namespace Mostlylucid.DataSummarizer.Tests;

/// <summary>
/// Tests for PII (Personally Identifiable Information) detection
/// </summary>
public class PiiDetectorTests
{
    private readonly PiiDetector _detector = new();

    [Fact]
    public void ScanColumn_DetectsSSN()
    {
        var samples = new object?[] { "123-45-6789", "987-65-4321", "111-22-3333" };
        var result = _detector.ScanColumn("ssn_column", samples, 3);

        Assert.True(result.IsPii);
        Assert.Equal(PiiType.SSN, result.PrimaryType);
        Assert.True(result.Confidence > 0.5);
        Assert.Equal(PiiRiskLevel.Critical, result.RiskLevel);
    }

    [Fact]
    public void ScanColumn_DetectsEmail()
    {
        var samples = new object?[] { "john@example.com", "jane.doe@company.org", "test@test.co.uk" };
        var result = _detector.ScanColumn("email", samples, 3);

        Assert.True(result.IsPii);
        Assert.Equal(PiiType.Email, result.PrimaryType);
        Assert.True(result.Confidence > 0.5);
    }

    [Fact]
    public void ScanColumn_DetectsCreditCard()
    {
        var samples = new object?[] { "4111111111111111", "5500000000000004", "340000000000009" };
        var result = _detector.ScanColumn("card_number", samples, 3);

        Assert.True(result.IsPii);
        Assert.Equal(PiiType.CreditCard, result.PrimaryType);
        Assert.Equal(PiiRiskLevel.Critical, result.RiskLevel);
    }

    [Fact]
    public void ScanColumn_DetectsPhoneNumber()
    {
        var samples = new object?[] { "(555) 123-4567", "555-123-4567", "+1 555 123 4567" };
        var result = _detector.ScanColumn("phone", samples, 3);

        Assert.True(result.IsPii);
        Assert.Equal(PiiType.PhoneNumber, result.PrimaryType);
    }

    [Fact]
    public void ScanColumn_DetectsIPAddress()
    {
        var samples = new object?[] { "192.168.1.1", "10.0.0.1", "172.16.0.1" };
        var result = _detector.ScanColumn("ip_addr", samples, 3);

        Assert.True(result.IsPii);
        Assert.Equal(PiiType.IPAddress, result.PrimaryType);
    }

    [Fact]
    public void ScanColumn_DetectsFromColumnName_SSN()
    {
        // Even with non-matching data, column name "ssn" should trigger detection
        // Use values that won't match any other pattern
        var samples = new object?[] { "hello world", "foo bar", "test data" };
        var result = _detector.ScanColumn("customer_ssn", samples, 3);

        Assert.True(result.IsPii);
        Assert.Equal(PiiType.SSN, result.PrimaryType);
        Assert.True(result.Confidence > 0); // Lower confidence for name-only
    }

    [Fact]
    public void ScanColumn_DetectsFromColumnName_Email()
    {
        var samples = new object?[] { "not_an_email", "also_not", "nope" };
        var result = _detector.ScanColumn("user_email_address", samples, 3);

        Assert.True(result.IsPii);
        Assert.Equal(PiiType.Email, result.PrimaryType);
    }

    [Fact]
    public void ScanColumn_NoFalsePositivesOnNumericData()
    {
        var samples = new object?[] { "100", "200", "300", "400", "500" };
        var result = _detector.ScanColumn("quantity", samples, 5);

        // Simple numbers shouldn't trigger PII detection
        Assert.False(result.IsPii || result.PrimaryType == PiiType.SSN || result.PrimaryType == PiiType.CreditCard);
    }

    [Fact]
    public void ScanColumn_NoFalsePositivesOnRegularText()
    {
        var samples = new object?[] { "Apple", "Banana", "Cherry", "Date", "Elderberry" };
        var result = _detector.ScanColumn("fruit_name", samples, 5);

        Assert.False(result.IsPii);
    }

    [Fact]
    public void ScanColumn_HandlesNullValues()
    {
        var samples = new object?[] { null, "123-45-6789", null, "987-65-4321" };
        var result = _detector.ScanColumn("ssn", samples, 4);

        Assert.True(result.IsPii);
        Assert.Equal(PiiType.SSN, result.PrimaryType);
    }

    [Fact]
    public void ScanColumn_HandlesEmptyInput()
    {
        var samples = Array.Empty<object?>();
        var result = _detector.ScanColumn("empty_column", samples, 0);

        Assert.False(result.IsPii);
        Assert.Null(result.PrimaryType);
    }

    [Fact]
    public void ScanColumn_DetectsZipCode()
    {
        var samples = new object?[] { "12345", "90210", "10001-1234" };
        var result = _detector.ScanColumn("postal_code", samples, 3);

        // Zip codes are detected but low risk
        Assert.Contains(result.DetectedTypes, d => d.Type == PiiType.ZipCode);
    }

    [Fact]
    public void ScanColumn_DetectsUUID()
    {
        var samples = new object?[] 
        { 
            "550e8400-e29b-41d4-a716-446655440000",
            "6ba7b810-9dad-11d1-80b4-00c04fd430c8"
        };
        var result = _detector.ScanColumn("user_id", samples, 2);

        Assert.Contains(result.DetectedTypes, d => d.Type == PiiType.UUID);
    }

    [Fact]
    public void GeneratePiiAlerts_CreatesAlertsForPii()
    {
        var piiResults = new List<PiiScanResult>
        {
            new()
            {
                ColumnName = "ssn",
                IsPii = true,
                PrimaryType = PiiType.SSN,
                Confidence = 0.95,
                RiskLevel = PiiRiskLevel.Critical,
                DetectedTypes = [new PiiDetection { Type = PiiType.SSN, MatchRate = 0.95 }]
            }
        };

        var alerts = _detector.GeneratePiiAlerts(piiResults);

        Assert.Single(alerts);
        Assert.Equal(AlertSeverity.Error, alerts[0].Severity);
        Assert.Equal(AlertType.PiiDetected, alerts[0].Type);
        Assert.Contains("SSN", alerts[0].Message);
    }

    [Fact]
    public void RiskLevel_CriticalForSSN()
    {
        var samples = new object?[] { "123-45-6789" };
        var result = _detector.ScanColumn("ssn", samples, 1);

        Assert.Equal(PiiRiskLevel.Critical, result.RiskLevel);
    }

    [Fact]
    public void RiskLevel_HighForEmail()
    {
        var samples = new object?[] { "test@example.com" };
        var result = _detector.ScanColumn("email", samples, 1);

        // Email with high confidence should be High risk
        Assert.True(result.RiskLevel >= PiiRiskLevel.Medium);
    }

    [Fact]
    public void ScanColumn_DetectsFromNameWithEmptySamples()
    {
        // Regression test: name-based detection should work even with empty samples
        // This was a bug where early return prevented name-based detection
        var result = _detector.ScanColumn("Email", Array.Empty<object?>(), 0);

        Assert.True(result.IsPii);
        Assert.Equal(PiiType.Email, result.PrimaryType);
        Assert.True(result.DetectedTypes.Any(d => d.DetectedFromName));
    }

    [Fact]
    public void ScanColumn_DetectsFromNameWithNullSamples()
    {
        var result = _detector.ScanColumn("customer_phone", new object?[] { null, null }, 2);

        Assert.True(result.IsPii);
        Assert.Equal(PiiType.PhoneNumber, result.PrimaryType);
    }

    [Fact]
    public void ScanColumn_DetectsFullNameAsPersonName()
    {
        var result = _detector.ScanColumn("full_name", Array.Empty<object?>(), 100);

        // "full_name" in column name should trigger PersonName detection
        Assert.True(result.IsPii);
        Assert.Equal(PiiType.PersonName, result.PrimaryType);
    }

    [Fact]
    public void ScanColumn_DetectsFirstNameAsPersonName()
    {
        var result = _detector.ScanColumn("first_name", Array.Empty<object?>(), 100);

        Assert.True(result.IsPii);
        Assert.Equal(PiiType.PersonName, result.PrimaryType);
    }
}
