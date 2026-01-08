using System.Collections.Concurrent;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Mostlylucid.DataSummarizer.Models;

namespace Mostlylucid.DataSummarizer.Services;

/// <summary>
/// Optional ONNX-based sentinel that scores columns based on profile features.
/// You can plug in any ONNX model that accepts a single float feature vector.
/// If the model is absent or incompatible, the sentinel safely no-ops.
/// </summary>
public class OnnxSentinel : IDisposable
{
    private readonly string _modelPath;
    private readonly bool _verbose;
    private InferenceSession? _session;
    private string? _inputName;
    private string? _outputName;
    private int _featureLength;

    public OnnxSentinel(string modelPath, bool verbose = false)
    {
        _modelPath = modelPath;
        _verbose = verbose;
    }

    public bool IsAvailable
    {
        get
        {
            EnsureLoaded();
            return _session != null && _inputName != null && _outputName != null && _featureLength > 0;
        }
    }

    public List<DataInsight> ScoreColumns(DataProfile profile)
    {
        EnsureLoaded();
        if (!IsAvailable) return [];

        var insights = new ConcurrentBag<DataInsight>();

        profile.Columns.AsParallel().ForAll(col =>
        {
            try
            {
                var features = BuildFeatures(col, profile);
                if (features.Length != _featureLength) return;

                var score = Score(features);
                if (score < 0) return;

                // Heuristic thresholds: adjust per model if needed
                var level = score switch
                {
                    >= 0.8f => "High risk/interesting",
                    >= 0.6f => "Medium risk/interesting",
                    _ => "Low risk"
                };

                insights.Add(new DataInsight
                {
                    Title = $"Sentinel score for '{col.Name}'",
                    Description = $"{level} column per ONNX sentinel (score {score:F2}).",
                    Source = InsightSource.Statistical,
                    RelatedColumns = [col.Name]
                });
            }
            catch (Exception ex)
            {
                if (_verbose) Console.WriteLine($"[OnnxSentinel] Failed scoring {col.Name}: {ex.Message}");
            }
        });

        return insights.ToList();
    }

    private float Score(float[] features)
    {
        if (_session == null || _inputName == null || _outputName == null) return -1;

        var tensor = new DenseTensor<float>(features, new[] { 1, features.Length });
        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(_inputName, tensor) };
        using var results = _session.Run(inputs);
        var output = results.FirstOrDefault(r => r.Name == _outputName) ?? results.First();

        switch (output.Value)
        {
            case DenseTensor<float> t when t.Length > 0:
                return ClampScore(t[0]);
            case IEnumerable<float> enumerable:
                return ClampScore(enumerable.FirstOrDefault());
            case float f:
                return ClampScore(f);
            default:
                return -1;
        }
    }

    private static float ClampScore(float val)
    {
        if (float.IsNaN(val) || float.IsInfinity(val)) return -1;
        return Math.Max(0f, Math.Min(1f, val));
    }

    private void EnsureLoaded()
    {
        if (_session != null) return;
        if (!File.Exists(_modelPath))
        {
            if (_verbose) Console.WriteLine($"[OnnxSentinel] Model not found: {_modelPath}");
            return;
        }

        try
        {
            _session = new InferenceSession(_modelPath);
            var inputMeta = _session.InputMetadata.First();
            _inputName = inputMeta.Key;

            // Expect a 2D shape [1, featureLen]
            var dims = inputMeta.Value.Dimensions.ToArray();
            _featureLength = dims.Length == 2 ? dims[1] : dims.Last();

            _outputName = _session.OutputMetadata.First().Key;
            if (_verbose)
            {
                Console.WriteLine($"[OnnxSentinel] Loaded {_modelPath} (input: {_inputName}, features: {_featureLength})");
            }
        }
        catch (Exception ex)
        {
            if (_verbose) Console.WriteLine($"[OnnxSentinel] Failed to load model: {ex.Message}");
            _session = null;
        }
    }

    private static float[] BuildFeatures(ColumnProfile col, DataProfile profile)
    {
        // Fixed-length feature vector so ONNX models can be built consistently.
        // You can retrain your model expecting this layout:
        // [0] null_percent, [1] unique_percent, [2] stddev, [3] skewness,
        // [4] outlier_ratio, [5] imbalance_ratio, [6] avg_length, [7] max_length,
        // [8] is_numeric, [9] is_categorical, [10] is_date, [11] is_text
        var outlierRatio = col.Count > 0 ? (float)col.OutlierCount / col.Count : 0f;
        var imbalance = (float)(col.ImbalanceRatio ?? 1.0);

        return new[]
        {
            (float)col.NullPercent,
            (float)col.UniquePercent,
            (float)(col.StdDev ?? 0),
            (float)(col.Skewness ?? 0),
            outlierRatio,
            imbalance,
            (float)(col.AvgLength ?? 0),
            (float)(col.MaxLength ?? 0),
            col.InferredType == ColumnType.Numeric ? 1f : 0f,
            col.InferredType == ColumnType.Categorical ? 1f : 0f,
            col.InferredType == ColumnType.DateTime ? 1f : 0f,
            col.InferredType == ColumnType.Text ? 1f : 0f
        };
    }

    public void Dispose()
    {
        _session?.Dispose();
        _session = null;
    }
}
