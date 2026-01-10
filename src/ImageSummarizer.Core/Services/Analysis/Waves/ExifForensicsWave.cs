using Mostlylucid.DocSummarizer.Images.Models.Dynamic;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;

namespace Mostlylucid.DocSummarizer.Images.Services.Analysis.Waves;

/// <summary>
/// Forensics wave for EXIF metadata analysis and tampering detection.
/// Extracts and validates EXIF data to detect manipulation indicators.
/// </summary>
public class ExifForensicsWave : IAnalysisWave
{
    public string Name => "ExifForensicsWave";
    public int Priority => 90; // High priority - provides metadata for other waves
    public IReadOnlyList<string> Tags => new[] { SignalTags.Forensic, SignalTags.Metadata };

    public async Task<IEnumerable<Signal>> AnalyzeAsync(
        string imagePath,
        AnalysisContext context,
        CancellationToken ct = default)
    {
        var signals = new List<Signal>();

        await using var stream = File.OpenRead(imagePath);
        using var image = await Image.LoadAsync(stream, ct);

        var exifProfile = image.Metadata.ExifProfile;

        // Basic EXIF presence
        signals.Add(new Signal
        {
            Key = "metadata.has_exif",
            Value = exifProfile != null,
            Confidence = 1.0,
            Source = Name,
            Tags = new List<string> { SignalTags.Metadata }
        });

        if (exifProfile == null)
        {
            return signals;
        }

        // Extract camera information
        string? make = null;
        string? model = null;

        if (exifProfile.TryGetValue(ExifTag.Make, out var makeValue))
        {
            make = makeValue?.Value?.Trim();
        }

        if (exifProfile.TryGetValue(ExifTag.Model, out var modelValue))
        {
            model = modelValue?.Value?.Trim();
        }

        if (!string.IsNullOrWhiteSpace(make) && !string.IsNullOrWhiteSpace(model))
        {
            signals.Add(new Signal
            {
                Key = "metadata.camera_info",
                Value = $"{make} {model}",
                Confidence = 1.0,
                Source = Name,
                Tags = new List<string> { SignalTags.Metadata },
                Metadata = new Dictionary<string, object>
                {
                    ["make"] = make,
                    ["model"] = model
                }
            });
        }

        // Original capture timestamp
        string? dateTimeOriginal = null;
        string? dateTimeDigitized = null;
        string? dateTime = null;

        if (exifProfile.TryGetValue(ExifTag.DateTimeOriginal, out var dateTimeOrigValue))
        {
            dateTimeOriginal = dateTimeOrigValue?.Value;
        }

        if (exifProfile.TryGetValue(ExifTag.DateTimeDigitized, out var dateTimeDigValue))
        {
            dateTimeDigitized = dateTimeDigValue?.Value;
        }

        if (exifProfile.TryGetValue(ExifTag.DateTime, out var dateTimeValue))
        {
            dateTime = dateTimeValue?.Value;
        }

        if (!string.IsNullOrWhiteSpace(dateTimeOriginal))
        {
            signals.Add(new Signal
            {
                Key = "metadata.datetime_original",
                Value = dateTimeOriginal,
                Confidence = 1.0,
                Source = Name,
                Tags = new List<string> { SignalTags.Metadata }
            });
        }

        // GPS coordinates
        Rational[]? gpsLat = null;
        Rational[]? gpsLon = null;

        if (exifProfile.TryGetValue(ExifTag.GPSLatitude, out var gpsLatValue))
        {
            gpsLat = gpsLatValue?.Value;
        }

        if (exifProfile.TryGetValue(ExifTag.GPSLongitude, out var gpsLonValue))
        {
            gpsLon = gpsLonValue?.Value;
        }

        if (gpsLat != null && gpsLon != null)
        {
            var latitude = ConvertToDecimal(gpsLat);
            var longitude = ConvertToDecimal(gpsLon);

            signals.Add(new Signal
            {
                Key = "metadata.gps_location",
                Value = new { latitude, longitude },
                Confidence = 1.0,
                Source = Name,
                Tags = new List<string> { SignalTags.Metadata },
                Metadata = new Dictionary<string, object>
                {
                    ["has_location"] = true
                }
            });
        }

        // Software used (editing detection)
        string? software = null;
        if (exifProfile.TryGetValue(ExifTag.Software, out var softwareValue))
        {
            software = softwareValue?.Value?.Trim();
        }

        if (!string.IsNullOrWhiteSpace(software))
        {
            var isEditingSoftware = IsKnownEditingSoftware(software);

            signals.Add(new Signal
            {
                Key = "metadata.software",
                Value = software,
                Confidence = 1.0,
                Source = Name,
                Tags = new List<string> { SignalTags.Metadata, SignalTags.Forensic },
                Metadata = new Dictionary<string, object>
                {
                    ["is_editing_software"] = isEditingSoftware
                }
            });

            if (isEditingSoftware)
            {
                signals.Add(new Signal
                {
                    Key = "forensics.possibly_edited",
                    Value = true,
                    Confidence = 0.7,
                    Source = Name,
                    Tags = new List<string> { SignalTags.Forensic },
                    Metadata = new Dictionary<string, object>
                    {
                        ["reason"] = "editing_software_detected",
                        ["software"] = software
                    }
                });
            }
        }

        // Tampering detection: Check for timestamp inconsistencies
        var tamperingSignals = DetectTimestampTampering(
            dateTimeOriginal,
            dateTimeDigitized,
            dateTime);

        signals.AddRange(tamperingSignals);

        // Orientation
        if (exifProfile.TryGetValue(ExifTag.Orientation, out var orientationValue))
        {
            var orientation = orientationValue?.Value;
            if (orientation.HasValue)
            {
                signals.Add(new Signal
                {
                    Key = "metadata.orientation",
                    Value = (int)orientation.Value,
                    Confidence = 1.0,
                    Source = Name,
                    Tags = new List<string> { SignalTags.Metadata }
                });
            }
        }

        // Image dimensions from EXIF (for comparison with actual)
        uint? exifWidth = null;
        uint? exifHeight = null;

        if (exifProfile.TryGetValue(ExifTag.PixelXDimension, out var widthValue))
        {
            exifWidth = (uint?)widthValue?.Value;
        }

        if (exifProfile.TryGetValue(ExifTag.PixelYDimension, out var heightValue))
        {
            exifHeight = (uint?)heightValue?.Value;
        }

        if (exifWidth.HasValue && exifHeight.HasValue)
        {
            var actualWidth = context.GetValue<int>("identity.width");
            var actualHeight = context.GetValue<int>("identity.height");

            if (actualWidth > 0 && actualHeight > 0)
            {
                if (exifWidth.Value != actualWidth || exifHeight.Value != actualHeight)
                {
                    signals.Add(new Signal
                    {
                        Key = "forensics.dimension_mismatch",
                        Value = true,
                        Confidence = 0.8,
                        Source = Name,
                        Tags = new List<string> { SignalTags.Forensic },
                        Metadata = new Dictionary<string, object>
                        {
                            ["exif_dimensions"] = $"{exifWidth}x{exifHeight}",
                            ["actual_dimensions"] = $"{actualWidth}x{actualHeight}",
                            ["reason"] = "Image may have been resized without updating EXIF"
                        }
                    });
                }
            }
        }

        return signals;
    }

    private static double ConvertToDecimal(Rational[] coordinates)
    {
        if (coordinates.Length < 3) return 0;

        var degrees = coordinates[0].ToDouble();
        var minutes = coordinates[1].ToDouble();
        var seconds = coordinates[2].ToDouble();

        return degrees + (minutes / 60.0) + (seconds / 3600.0);
    }

    private static bool IsKnownEditingSoftware(string software)
    {
        var editingSoftware = new[]
        {
            "photoshop", "gimp", "paint.net", "affinity", "pixelmator",
            "snapseed", "lightroom", "capture one", "darktable", "rawtherapee"
        };

        return editingSoftware.Any(s => software.ToLowerInvariant().Contains(s));
    }

    private List<Signal> DetectTimestampTampering(
        string? dateTimeOriginal,
        string? dateTimeDigitized,
        string? dateTime)
    {
        var signals = new List<Signal>();

        if (string.IsNullOrWhiteSpace(dateTimeOriginal) ||
            string.IsNullOrWhiteSpace(dateTimeDigitized))
        {
            return signals;
        }

        try
        {
            // Parse timestamps
            var original = DateTime.ParseExact(dateTimeOriginal, "yyyy:MM:dd HH:mm:ss", null);
            var digitized = DateTime.ParseExact(dateTimeDigitized, "yyyy:MM:dd HH:mm:ss", null);

            // Original should be <= Digitized (capture before digital conversion)
            if (original > digitized)
            {
                signals.Add(new Signal
                {
                    Key = "forensics.timestamp_tampering",
                    Value = true,
                    Confidence = 0.9,
                    Source = Name,
                    Tags = new List<string> { SignalTags.Forensic },
                    Metadata = new Dictionary<string, object>
                    {
                        ["reason"] = "Original timestamp is after digitized timestamp",
                        ["datetime_original"] = dateTimeOriginal,
                        ["datetime_digitized"] = dateTimeDigitized
                    }
                });
            }

            // Check if timestamps are in the future
            if (original > DateTime.Now || digitized > DateTime.Now)
            {
                signals.Add(new Signal
                {
                    Key = "forensics.future_timestamp",
                    Value = true,
                    Confidence = 0.95,
                    Source = Name,
                    Tags = new List<string> { SignalTags.Forensic },
                    Metadata = new Dictionary<string, object>
                    {
                        ["reason"] = "Timestamp is in the future"
                    }
                });
            }

            // Check for suspiciously round timestamps (00:00:00)
            if (original.Hour == 0 && original.Minute == 0 && original.Second == 0)
            {
                signals.Add(new Signal
                {
                    Key = "forensics.suspicious_timestamp",
                    Value = true,
                    Confidence = 0.6,
                    Source = Name,
                    Tags = new List<string> { SignalTags.Forensic },
                    Metadata = new Dictionary<string, object>
                    {
                        ["reason"] = "Timestamp is exactly midnight (possibly fabricated)"
                    }
                });
            }
        }
        catch
        {
            // Invalid timestamp format - potential tampering
            signals.Add(new Signal
            {
                Key = "forensics.invalid_timestamp",
                Value = true,
                Confidence = 0.7,
                Source = Name,
                Tags = new List<string> { SignalTags.Forensic },
                Metadata = new Dictionary<string, object>
                {
                    ["reason"] = "Timestamp format is invalid"
                }
            });
        }

        return signals;
    }
}
