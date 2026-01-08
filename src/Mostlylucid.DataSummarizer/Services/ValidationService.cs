using Mostlylucid.DataSummarizer.Models;

namespace Mostlylucid.DataSummarizer.Services;

public class ValidationResult
{
    public string Source { get; set; } = "";
    public string Target { get; set; } = "";
    public List<ColumnDelta> Columns { get; set; } = [];
    public double DriftScore { get; set; } // 0-1 (higher = more drift)
}

public class ColumnDelta
{
    public string Name { get; set; } = "";
    public ColumnType Type { get; set; }
    public double? NullDelta { get; set; }
    public double? MeanDelta { get; set; }
    public double? StdDelta { get; set; }
    public double? MadDelta { get; set; }
    public double? TopOverlap { get; set; }
    public string Note { get; set; } = "";
}

public static class ValidationService
{
    public static ValidationResult Compare(DataProfile src, DataProfile tgt)
    {
        var result = new ValidationResult { Source = src.SourcePath, Target = tgt.SourcePath };

        double maxScore = 0;
        foreach (var sCol in src.Columns)
        {
            var tCol = tgt.Columns.FirstOrDefault(c => c.Name.Equals(sCol.Name, StringComparison.OrdinalIgnoreCase));
            if (tCol == null)
            {
                result.Columns.Add(new ColumnDelta { Name = sCol.Name, Type = sCol.InferredType, Note = "missing in target", NullDelta = null });
                maxScore = Math.Max(maxScore, 1.0);
                continue;
            }

            var delta = new ColumnDelta { Name = sCol.Name, Type = sCol.InferredType };
            delta.NullDelta = tCol.NullPercent - sCol.NullPercent;

            if (sCol.InferredType == ColumnType.Numeric)
            {
                delta.MeanDelta = SafeDiff(tCol.Mean, sCol.Mean);
                delta.StdDelta = SafeDiff(tCol.StdDev, sCol.StdDev);
                delta.MadDelta = SafeDiff(tCol.Mad, sCol.Mad);
                maxScore = Math.Max(maxScore, Score(delta.MeanDelta) + Score(delta.StdDelta) + Score(delta.MadDelta));
            }
            else if (sCol.InferredType == ColumnType.Categorical)
            {
                delta.TopOverlap = TopOverlap(sCol.TopValues, tCol.TopValues);
                maxScore = Math.Max(maxScore, 1 - (delta.TopOverlap ?? 0));
            }

            result.Columns.Add(delta);
        }

        result.DriftScore = Math.Min(1.0, maxScore);
        return result;
    }

    private static double Score(double? v)
    {
        if (!v.HasValue) return 0;
        var a = Math.Abs(v.Value);
        if (a < 0.01) return 0;
        if (a < 0.05) return 0.1;
        if (a < 0.1) return 0.3;
        if (a < 0.2) return 0.6;
        return 1.0;
    }

    private static double? SafeDiff(double? a, double? b) => (a.HasValue && b.HasValue) ? a.Value - b.Value : null;

    private static double? TopOverlap(List<ValueCount>? a, List<ValueCount>? b)
    {
        if (a is null || b is null || a.Count == 0 || b.Count == 0) return null;
        var topA = a.Select(v => v.Value).Take(5).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var topB = b.Select(v => v.Value).Take(5).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var inter = topA.Intersect(topB, StringComparer.OrdinalIgnoreCase).Count();
        var union = topA.Union(topB, StringComparer.OrdinalIgnoreCase).Count();
        return union == 0 ? 0 : inter * 1.0 / union;
    }
}
