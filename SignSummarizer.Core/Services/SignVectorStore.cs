using Microsoft.Extensions.Logging;
using SignSummarizer.Models;

namespace SignSummarizer.Services;

public interface ISignVectorStore
{
    Task StoreAsync(SignAtom atom, string? signerId = null, CancellationToken cancellationToken = default);
    Task<IList<SignMatch>> FindMatchesAsync(float[] embedding, int topK = 5, string? signerId = null, CancellationToken cancellationToken = default);
    Task<IList<SignMatch>> FindMatchesAsync(SignAtom atom, int topK = 5, string? signerId = null, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid atomId, CancellationToken cancellationToken = default);
    Task ClearSignerAsync(string signerId, CancellationToken cancellationToken = default);
}

public sealed record SignMatch(
    Guid AtomId,
    float Similarity,
    SignAtom Atom,
    string? SignerId
);

public sealed class SignVectorStore : ISignVectorStore, IDisposable
{
    private readonly ILogger<SignVectorStore> _logger;
    private readonly Dictionary<string, List<StoredAtom>> _store;
    private readonly object _lock = new();
    private const int DefaultEmbeddingDimensions = 128;
    
    public SignVectorStore(ILogger<SignVectorStore> logger)
    {
        _logger = logger;
        _store = new Dictionary<string, List<StoredAtom>>();
    }
    
    public async Task StoreAsync(SignAtom atom, string? signerId = null, CancellationToken cancellationToken = default)
    {
        if (atom.PoseEmbedding == null || atom.PoseEmbedding.Length == 0)
            throw new ArgumentException("Atom must have an embedding to store");
        
        var key = signerId ?? "global";
        
        var stored = new StoredAtom(
            atom.Id,
            atom.PoseEmbedding,
            atom,
            signerId);
        
        lock (_lock)
        {
            if (!_store.TryGetValue(key, out var atoms))
            {
                atoms = new List<StoredAtom>();
                _store[key] = atoms;
            }
            
            atoms.Add(stored);
        }
        
        _logger.LogInformation("Stored atom {AtomId} for signer {SignerId}", atom.Id, key);
    }
    
    public async Task<IList<SignMatch>> FindMatchesAsync(
        float[] embedding,
        int topK = 5,
        string? signerId = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<SignMatch>();
        
        var keys = signerId != null 
            ? new[] { signerId }
            : _store.Keys.ToArray();
        
        lock (_lock)
        {
            foreach (var key in keys)
            {
                if (!_store.TryGetValue(key, out var atoms))
                    continue;
                
                foreach (var stored in atoms)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;
                    
                    var similarity = CosineSimilarity(embedding, stored.Embedding);
                    
                    results.Add(new SignMatch(
                        stored.AtomId,
                        similarity,
                        stored.Atom,
                        stored.SignerId));
                }
            }
        }
        
        return results
            .OrderByDescending(m => m.Similarity)
            .Take(topK)
            .ToList();
    }
    
    public async Task<IList<SignMatch>> FindMatchesAsync(
        SignAtom atom,
        int topK = 5,
        string? signerId = null,
        CancellationToken cancellationToken = default)
    {
        if (atom.PoseEmbedding == null || atom.PoseEmbedding.Length == 0)
            throw new ArgumentException("Atom must have an embedding");
        
        return await FindMatchesAsync(atom.PoseEmbedding, topK, signerId, cancellationToken);
    }
    
    public async Task DeleteAsync(Guid atomId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            foreach (var (key, atoms) in _store.ToList())
            {
                atoms.RemoveAll(a => a.AtomId == atomId);
                
                if (atoms.Count == 0)
                    _store.Remove(key);
            }
        }
        
        _logger.LogInformation("Deleted atom {AtomId}", atomId);
    }
    
    public async Task ClearSignerAsync(string signerId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _store.Remove(signerId);
        }
        
        _logger.LogInformation("Cleared all atoms for signer {SignerId}", signerId);
    }
    
    private static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length)
            return 0f;
        
        var dot = 0f;
        var magA = 0f;
        var magB = 0f;
        
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }
        
        var magnitude = MathF.Sqrt(magA) * MathF.Sqrt(magB);
        
        return magnitude > 0 ? dot / magnitude : 0f;
    }
    
    private sealed record StoredAtom(
        Guid AtomId,
        float[] Embedding,
        SignAtom Atom,
        string? SignerId
    );
    
    public void Dispose()
    {
        lock (_lock)
        {
            _store.Clear();
        }
    }
}