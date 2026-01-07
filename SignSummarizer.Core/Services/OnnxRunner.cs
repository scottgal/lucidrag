using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace SignSummarizer.Services;

public sealed class OnnxRunner : IDisposable
{
    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly string _outputName;
    private readonly int[] _inputShape;
    
    public OnnxRunner(string modelPath, string inputName, string outputName, int[] inputShape)
    {
        _session = new InferenceSession(modelPath);
        _inputName = inputName;
        _outputName = outputName;
        _inputShape = inputShape;
    }
    
    public float[] Run(ReadOnlySpan<float> input)
    {
        var tensor = new DenseTensor<float>(_inputShape);
        input.CopyTo(tensor.Buffer.Span);
        
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(_inputName, tensor)
        };
        
        using var results = _session.Run(inputs);
        var output = results.First(x => x.Name == _outputName).AsTensor<float>();
        
        return output.ToArray();
    }
    
    public Dictionary<string, float[]> Run(Dictionary<string, float[]> inputs)
    {
        var namedInputs = new List<NamedOnnxValue>();
        
        foreach (var (name, data) in inputs)
        {
            var tensor = new DenseTensor<float>(data.Length);
            data.AsSpan().CopyTo(tensor.Buffer.Span);
            namedInputs.Add(NamedOnnxValue.CreateFromTensor(name, tensor));
        }
        
        var results = _session.Run(namedInputs);
        var outputs = new Dictionary<string, float[]>();
        
        foreach (var result in results)
        {
            outputs[result.Name] = result.AsTensor<float>().ToArray();
        }
        
        return outputs;
    }
    
    public void Dispose()
    {
        _session.Dispose();
    }
}