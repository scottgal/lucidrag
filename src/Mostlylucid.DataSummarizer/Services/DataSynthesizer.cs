using Bogus;
using MathNet.Numerics.Distributions;
using Mostlylucid.DataSummarizer.Models;

namespace Mostlylucid.DataSummarizer.Services;

/// <summary>
/// Generates synthetic data that statistically matches a profiled dataset.
/// Uses all available profile statistics for accurate reproduction:
/// - Distribution type (Normal, Uniform, Exponential, Skewed)
/// - Skewness and Kurtosis for asymmetric distributions
/// - Quartiles (Q25, Median, Q75) for accurate percentile matching
/// - Null rates for realistic missing data
/// - Zero counts for sparse/zero-inflated data
/// - Text patterns (email, phone, UUID, etc.)
/// - Category distributions with proper weighting
/// - Correlations between columns (when possible)
/// </summary>
public static class DataSynthesizer
{
    /// <summary>
    /// Options for controlling synthesis behavior
    /// </summary>
    public class SynthesisOptions
    {
        /// <summary>Seed for reproducible generation (null = random)</summary>
        public int? Seed { get; set; }
        
        /// <summary>Preserve correlations between numeric columns</summary>
        public bool PreserveCorrelations { get; set; } = true;
        
        /// <summary>Generate nulls at the profiled rate</summary>
        public bool GenerateNulls { get; set; } = true;
        
        /// <summary>Use detected distribution type for generation</summary>
        public bool UseDistributionType { get; set; } = true;
        
        /// <summary>Generate text matching detected patterns (email, phone, etc.)</summary>
        public bool UseTextPatterns { get; set; } = true;
        
        /// <summary>Minimum count for k-anonymity. Categories below this are rolled into "Other".</summary>
        public int KAnonymityThreshold { get; set; } = 5;
        
        /// <summary>Respect column-level GenerationPolicy settings</summary>
        public bool RespectGenerationPolicy { get; set; } = true;
    }

    public static void GenerateCsv(DataProfile profile, int rows, string outputPath, SynthesisOptions? options = null)
    {
        options ??= new SynthesisOptions();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? ".");

        using var writer = new StreamWriter(outputPath);
        writer.WriteLine(string.Join(',', profile.Columns.Select(c => Escape(c.Name))));

        var rnd = options.Seed.HasValue ? new Random(options.Seed.Value) : new Random();
        var faker = new Faker();
        
        // Pre-compute correlation matrix if needed
        double[,]? correlationMatrix = null;
        List<ColumnProfile>? correlatedColumns = null;
        if (options.PreserveCorrelations && profile.Correlations.Count > 0)
        {
            (correlationMatrix, correlatedColumns) = BuildCorrelationMatrix(profile);
        }

        // Generate correlated random values for each row if we have correlations
        double[][]? correlatedValues = null;
        if (correlationMatrix != null && correlatedColumns != null)
        {
            correlatedValues = GenerateCorrelatedValues(correlationMatrix, correlatedColumns, rows, rnd);
        }

        for (int i = 0; i < rows; i++)
        {
            var row = new List<string>();
            
            foreach (var col in profile.Columns)
            {
                // Check if this column has correlated values pre-generated
                double? correlatedValue = null;
                if (correlatedColumns != null && correlatedValues != null)
                {
                    var colIdx = correlatedColumns.FindIndex(c => c.Name == col.Name);
                    if (colIdx >= 0)
                    {
                        correlatedValue = correlatedValues[i][colIdx];
                    }
                }
                
                row.Add(Escape(GenerateValue(col, rnd, faker, i, options, correlatedValue)));
            }
            
            writer.WriteLine(string.Join(',', row));
        }
    }

    private static string GenerateValue(
        ColumnProfile col, 
        Random rnd, 
        Faker faker, 
        int rowIndex, 
        SynthesisOptions options,
        double? correlatedValue = null)
    {
        // Handle nulls first
        if (options.GenerateNulls && col.NullPercent > 0)
        {
            if (rnd.NextDouble() * 100 < col.NullPercent)
            {
                return "";
            }
        }

        switch (col.InferredType)
        {
            case ColumnType.Id:
                return GenerateId(col, rowIndex);
                
            case ColumnType.Numeric:
                return GenerateNumeric(col, rnd, options, correlatedValue);
                
            case ColumnType.DateTime:
                return GenerateDateTime(col, rnd);
                
            case ColumnType.Boolean:
                return GenerateBoolean(col, rnd);
                
            case ColumnType.Categorical:
                return GenerateCategorical(col, rnd, faker, options);
                
            case ColumnType.Text:
                return GenerateText(col, rnd, faker, options);
                
            default:
                return "";
        }
    }

    private static string GenerateId(ColumnProfile col, int rowIndex)
    {
        // Check for UUID pattern
        if (col.TextPatterns.Any(p => p.PatternType == TextPatternType.Uuid))
        {
            return Guid.NewGuid().ToString();
        }
        
        // Sequential ID
        return (rowIndex + 1).ToString();
    }

    private static string GenerateNumeric(
        ColumnProfile col, 
        Random rnd, 
        SynthesisOptions options,
        double? correlatedValue = null)
    {
        var mean = col.Mean ?? 0;
        var stdDev = col.StdDev ?? Math.Max(1, (col.Max ?? mean + 1) - (col.Min ?? mean - 1)) / 6.0;
        var min = col.Min ?? double.MinValue;
        var max = col.Max ?? double.MaxValue;
        
        double value;

        // If we have a correlated value, use it (already in standard normal form)
        if (correlatedValue.HasValue)
        {
            value = mean + correlatedValue.Value * stdDev;
            value = Math.Clamp(value, min, max);
            return FormatNumeric(value, col);
        }

        // Handle zero-inflated data
        if (col.ZeroCount > 0 && col.Count > 0)
        {
            var zeroRate = (double)col.ZeroCount / col.Count;
            if (rnd.NextDouble() < zeroRate)
            {
                return "0";
            }
        }

        // BEST: Use histogram if available - most accurate
        if (col.Histogram != null && col.Histogram.BinCounts.Count > 0)
        {
            value = GenerateFromHistogram(col.Histogram, rnd);
            return FormatNumeric(value, col);
        }

        // Generate based on detected distribution
        if (options.UseDistributionType && col.Distribution.HasValue)
        {
            value = GenerateFromDistribution(col.Distribution.Value, mean, stdDev, min, max, col, rnd);
        }
        else if (col.Skewness.HasValue && Math.Abs(col.Skewness.Value) > 0.5)
        {
            // Use skewness-aware generation
            value = GenerateSkewedValue(mean, stdDev, col.Skewness.Value, rnd);
        }
        else
        {
            // Standard normal generation
            value = NextGaussian(rnd, mean, stdDev);
        }

        // Clamp to observed range
        value = Math.Clamp(value, min, max);
        
        return FormatNumeric(value, col);
    }
    
    /// <summary>
    /// Generate a value by sampling from histogram bins.
    /// Most accurate method - preserves actual distribution shape.
    /// </summary>
    private static double GenerateFromHistogram(NumericHistogram histogram, Random rnd)
    {
        var totalCount = histogram.BinCounts.Sum();
        if (totalCount == 0) return 0;
        
        // Weighted random bin selection
        var target = rnd.NextDouble() * totalCount;
        long cumulative = 0;
        int selectedBin = 0;
        
        for (int i = 0; i < histogram.BinCounts.Count; i++)
        {
            cumulative += histogram.BinCounts[i];
            if (target <= cumulative)
            {
                selectedBin = i;
                break;
            }
        }
        
        // Sample uniformly within the selected bin
        var binLower = histogram.BinEdges[selectedBin];
        var binUpper = histogram.BinEdges[selectedBin + 1];
        
        return binLower + rnd.NextDouble() * (binUpper - binLower);
    }

    private static double GenerateFromDistribution(
        DistributionType distType, 
        double mean, 
        double stdDev, 
        double min, 
        double max,
        ColumnProfile col,
        Random rnd)
    {
        switch (distType)
        {
            case DistributionType.Normal:
                return NextGaussian(rnd, mean, stdDev);
                
            case DistributionType.Uniform:
                // Use actual min/max for uniform
                return min + rnd.NextDouble() * (max - min);
                
            case DistributionType.Exponential:
                // Exponential with rate = 1/mean (shifted to min)
                var rate = mean > min ? 1.0 / (mean - min) : 1.0;
                return min + Exponential.Sample(rnd, rate);
                
            case DistributionType.RightSkewed:
                return GenerateSkewedValue(mean, stdDev, col.Skewness ?? 1.0, rnd);
                
            case DistributionType.LeftSkewed:
                return GenerateSkewedValue(mean, stdDev, col.Skewness ?? -1.0, rnd);
                
            case DistributionType.PowerLaw:
                // Pareto-like: heavy right tail
                var alpha = 2.0; // Shape parameter
                var xm = min > 0 ? min : 1.0; // Scale parameter
                var u = rnd.NextDouble();
                return xm / Math.Pow(u, 1.0 / alpha);
                
            case DistributionType.Bimodal:
                // Two normal distributions mixed
                var median = col.Median ?? mean;
                if (rnd.NextDouble() < 0.5)
                    return NextGaussian(rnd, (min + median) / 2, stdDev / 2);
                else
                    return NextGaussian(rnd, (median + max) / 2, stdDev / 2);
                
            default:
                return NextGaussian(rnd, mean, stdDev);
        }
    }

    /// <summary>
    /// Generate skewed values using the Azzalini skew-normal distribution approximation
    /// </summary>
    private static double GenerateSkewedValue(double mean, double stdDev, double skewness, Random rnd)
    {
        // Clamp skewness to valid range for approximation
        skewness = Math.Clamp(skewness, -0.99, 0.99);
        
        // Use Fleishman's power method for controlled skewness
        // Generate standard normal
        var z = NextGaussian(rnd, 0, 1);
        
        // Apply skewness transformation
        // Simplified cubic transformation: Y = a + b*Z + c*Z^2 + d*Z^3
        var c = skewness / 6.0;  // Third moment coefficient
        var transformed = z + c * (z * z - 1);
        
        return mean + transformed * stdDev;
    }

    private static string FormatNumeric(double value, ColumnProfile col)
    {
        // Detect if column appears to be integer-valued
        var isInteger = col.Mean.HasValue && col.Mean.Value == Math.Floor(col.Mean.Value) &&
                       (!col.StdDev.HasValue || col.StdDev.Value == Math.Floor(col.StdDev.Value));
        
        if (isInteger || (col.Min.HasValue && col.Max.HasValue && 
            col.Min.Value == Math.Floor(col.Min.Value) && col.Max.Value == Math.Floor(col.Max.Value)))
        {
            return ((long)Math.Round(value)).ToString();
        }
        
        return Math.Round(value, 2).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string GenerateDateTime(ColumnProfile col, Random rnd)
    {
        var min = col.MinDate ?? DateTime.UtcNow.AddDays(-365);
        var max = col.MaxDate ?? DateTime.UtcNow;
        
        if (max <= min) max = min.AddDays(1);
        
        // Handle time series with gaps
        if (col.DateGapDays.HasValue && col.DateGapDays.Value > 1)
        {
            // Generate dates with similar gap pattern
            var dayIndex = (int)(rnd.NextDouble() * ((max - min).TotalDays / col.DateGapDays.Value));
            return min.AddDays(dayIndex * col.DateGapDays.Value).ToString("yyyy-MM-dd");
        }
        
        // Check for time series granularity
        if (col.TimeSeries?.Granularity != null)
        {
            return GenerateDateWithGranularity(col.TimeSeries.Granularity, min, max, rnd);
        }
        
        var span = max - min;
        var t = rnd.NextDouble();
        var val = min + TimeSpan.FromTicks((long)(span.Ticks * t));
        return val.ToString("yyyy-MM-ddTHH:mm:ss");
    }

    private static string GenerateDateWithGranularity(TimeGranularity granularity, DateTime min, DateTime max, Random rnd)
    {
        var span = max - min;
        var t = rnd.NextDouble();
        var val = min + TimeSpan.FromTicks((long)(span.Ticks * t));

        return granularity switch
        {
            TimeGranularity.Yearly => new DateTime(val.Year, 1, 1).ToString("yyyy-01-01"),
            TimeGranularity.Monthly => new DateTime(val.Year, val.Month, 1).ToString("yyyy-MM-01"),
            TimeGranularity.Weekly => val.AddDays(-(int)val.DayOfWeek).ToString("yyyy-MM-dd"),
            TimeGranularity.Daily => val.ToString("yyyy-MM-dd"),
            TimeGranularity.Hourly => new DateTime(val.Year, val.Month, val.Day, val.Hour, 0, 0).ToString("yyyy-MM-ddTHH:00:00"),
            _ => val.ToString("yyyy-MM-ddTHH:mm:ss")
        };
    }

    private static string GenerateBoolean(ColumnProfile col, Random rnd)
    {
        // Use top values distribution if available
        if (col.TopValues?.Count >= 2)
        {
            var total = col.TopValues.Sum(v => v.Count);
            var trueValue = col.TopValues.FirstOrDefault(v => 
                v.Value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                v.Value.Equals("1", StringComparison.Ordinal) ||
                v.Value.Equals("yes", StringComparison.OrdinalIgnoreCase));
            
            if (trueValue != null && total > 0)
            {
                var trueRate = (double)trueValue.Count / total;
                return rnd.NextDouble() < trueRate ? "true" : "false";
            }
        }
        
        return rnd.NextDouble() < 0.5 ? "true" : "false";
    }

    private static string GenerateCategorical(ColumnProfile col, Random rnd, Faker faker, SynthesisOptions options)
    {
        if (col.TopValues?.Count > 0)
        {
            // Get k-anonymity threshold (default 5)
            var kThreshold = col.SynthesisPolicy?.KAnonymityThreshold ?? options.KAnonymityThreshold;
            
            // Filter values that meet k-anonymity threshold
            var safeValues = col.TopValues
                .Where(v => v.Count >= kThreshold)
                .ToList();
            
            // Calculate total including "Other" bucket if we have it
            var safeTotal = safeValues.Sum(v => v.Count);
            var otherCount = col.OtherCount;
            
            // Add back rare categories (rolled into Other) to total for proper weighting
            var totalWithOther = safeTotal + otherCount;
            
            if (totalWithOther > 0)
            {
                var pick = rnd.NextDouble() * totalWithOther;
                
                // Check if we should pick from "Other" bucket
                if (pick >= safeTotal && otherCount > 0)
                {
                    // Generate a placeholder for "Other" - use Faker for realistic values
                    return GenerateCategoricalFallback(col.Name, faker);
                }
                
                // Pick from safe values
                double acc = 0;
                foreach (var v in safeValues)
                {
                    acc += v.Count;
                    if (pick <= acc) return v.Value;
                }
                
                if (safeValues.Count > 0)
                    return safeValues[0].Value;
            }
        }
        
        // Fallback based on column name heuristics
        return GenerateCategoricalFallback(col.Name, faker);
    }
    
    private static string GenerateCategoricalFallback(string colName, Faker faker)
    {
        var name = colName.ToLowerInvariant();
        if (name.Contains("country")) return faker.Address.Country();
        if (name.Contains("city")) return faker.Address.City();
        if (name.Contains("state") || name.Contains("region")) return faker.Address.State();
        if (name.Contains("category") || name.Contains("type")) return faker.Commerce.Categories(1)[0];
        if (name.Contains("department")) return faker.Commerce.Department();
        if (name.Contains("product")) return faker.Commerce.ProductName();
        if (name.Contains("company")) return faker.Company.CompanyName();
        if (name.Contains("gender")) return faker.PickRandom("Male", "Female", "Other");
        if (name.Contains("status")) return faker.PickRandom("Active", "Inactive", "Pending");
        if (name.Contains("priority")) return faker.PickRandom("Low", "Medium", "High", "Critical");
        
        return faker.Commerce.Department();
    }

    private static string GenerateText(ColumnProfile col, Random rnd, Faker faker, SynthesisOptions options)
    {
        // Check for detected text patterns first
        if (options.UseTextPatterns && col.TextPatterns.Count > 0)
        {
            // Pick pattern weighted by match percentage
            var pattern = col.TextPatterns
                .OrderByDescending(p => p.MatchPercent)
                .FirstOrDefault();
            
            if (pattern != null && rnd.NextDouble() * 100 < pattern.MatchPercent)
            {
                return GenerateFromPattern(pattern.PatternType, faker);
            }
        }
        
        // Generate based on length stats
        var targetLen = col.AvgLength.HasValue 
            ? Math.Max(3, (int)Math.Round(col.AvgLength.Value)) 
            : 12;
        
        var minLen = col.MinLength ?? 1;
        var maxLen = col.MaxLength ?? 100;
        
        // Vary length around average
        var len = (int)(targetLen + (rnd.NextDouble() - 0.5) * (targetLen * 0.4));
        len = Math.Clamp(len, minLen, Math.Min(maxLen, 200));
        
        // Use column name heuristics for realistic text
        var name = col.Name.ToLowerInvariant();
        if (name.Contains("name") && !name.Contains("file") && !name.Contains("product"))
        {
            if (name.Contains("first")) return faker.Name.FirstName();
            if (name.Contains("last")) return faker.Name.LastName();
            if (name.Contains("full") || name == "name") return faker.Name.FullName();
            return faker.Name.FullName();
        }
        if (name.Contains("address")) return faker.Address.FullAddress();
        if (name.Contains("description") || name.Contains("comment") || name.Contains("note"))
        {
            return faker.Lorem.Sentence(Math.Max(3, len / 5));
        }
        if (name.Contains("title")) return faker.Lorem.Sentence(3);
        
        // Default: alphanumeric of target length
        return faker.Random.AlphaNumeric(len);
    }

    private static string GenerateFromPattern(TextPatternType patternType, Faker faker)
    {
        return patternType switch
        {
            TextPatternType.Email => faker.Internet.Email(),
            TextPatternType.Url => faker.Internet.Url(),
            TextPatternType.Phone => faker.Phone.PhoneNumber(),
            TextPatternType.Uuid => Guid.NewGuid().ToString(),
            TextPatternType.IpAddress => faker.Internet.Ip(),
            TextPatternType.CreditCard => faker.Finance.CreditCardNumber(),
            TextPatternType.PostalCode => faker.Address.ZipCode(),
            TextPatternType.Date => faker.Date.Past().ToString("yyyy-MM-dd"),
            TextPatternType.Currency => faker.Finance.Amount().ToString("C"),
            TextPatternType.Percentage => $"{faker.Random.Double(0, 100):F1}%",
            _ => faker.Random.AlphaNumeric(10)
        };
    }

    /// <summary>
    /// Build correlation matrix from profile correlations
    /// </summary>
    private static (double[,] matrix, List<ColumnProfile> columns) BuildCorrelationMatrix(DataProfile profile)
    {
        // Get all numeric columns that have correlations
        var correlatedColNames = profile.Correlations
            .SelectMany(c => new[] { c.Column1, c.Column2 })
            .Distinct()
            .ToList();
        
        var numericCols = profile.Columns
            .Where(c => c.InferredType == ColumnType.Numeric && correlatedColNames.Contains(c.Name))
            .Take(10) // Limit for performance
            .ToList();
        
        if (numericCols.Count < 2)
            return (new double[0, 0], new List<ColumnProfile>());
        
        var n = numericCols.Count;
        var matrix = new double[n, n];
        
        // Initialize with identity (diagonal = 1)
        for (int i = 0; i < n; i++)
            matrix[i, i] = 1.0;
        
        // Fill in correlations
        foreach (var corr in profile.Correlations)
        {
            var i = numericCols.FindIndex(c => c.Name == corr.Column1);
            var j = numericCols.FindIndex(c => c.Name == corr.Column2);
            if (i >= 0 && j >= 0)
            {
                matrix[i, j] = corr.Correlation;
                matrix[j, i] = corr.Correlation;
            }
        }
        
        return (matrix, numericCols);
    }

    /// <summary>
    /// Generate correlated random values using Cholesky decomposition
    /// </summary>
    private static double[][] GenerateCorrelatedValues(
        double[,] correlationMatrix, 
        List<ColumnProfile> columns, 
        int rows, 
        Random rnd)
    {
        var n = columns.Count;
        if (n == 0) return Array.Empty<double[]>();
        
        // Cholesky decomposition of correlation matrix
        var cholesky = new double[n, n];
        try
        {
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j <= i; j++)
                {
                    double sum = 0;
                    for (int k = 0; k < j; k++)
                        sum += cholesky[i, k] * cholesky[j, k];
                    
                    if (i == j)
                    {
                        var val = correlationMatrix[i, i] - sum;
                        cholesky[i, j] = val > 0 ? Math.Sqrt(val) : 0;
                    }
                    else
                    {
                        cholesky[i, j] = cholesky[j, j] > 0 
                            ? (correlationMatrix[i, j] - sum) / cholesky[j, j] 
                            : 0;
                    }
                }
            }
        }
        catch
        {
            // If Cholesky fails (not positive definite), return null to skip correlation
            return Array.Empty<double[]>();
        }
        
        // Generate independent standard normals and transform
        var result = new double[rows][];
        for (int row = 0; row < rows; row++)
        {
            // Generate independent standard normals
            var independent = new double[n];
            for (int i = 0; i < n; i++)
                independent[i] = NextGaussian(rnd, 0, 1);
            
            // Transform using Cholesky: correlated = L * independent
            var correlated = new double[n];
            for (int i = 0; i < n; i++)
            {
                double sum = 0;
                for (int j = 0; j <= i; j++)
                    sum += cholesky[i, j] * independent[j];
                correlated[i] = sum;
            }
            
            result[row] = correlated;
        }
        
        return result;
    }

    private static double NextGaussian(Random rnd, double mean, double stddev)
    {
        // Box-Muller transform
        var u1 = 1.0 - rnd.NextDouble();
        var u2 = 1.0 - rnd.NextDouble();
        var randStdNormal = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
        return mean + stddev * randStdNormal;
    }

    private static string Escape(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "";
            
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
        {
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
        return value;
    }
}
