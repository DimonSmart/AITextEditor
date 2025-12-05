using AiTextEditor.Lib.Interfaces;
using AiTextEditor.Lib.Model.Indexing;
using System.Linq;

namespace AiTextEditor.Lib.Services;

/// <summary>
/// Simple in-memory vector index with cosine similarity. Suitable for prototyping.
/// </summary>
public class InMemoryVectorIndex : IVectorIndex
{
    private readonly object sync = new();
    private readonly Dictionary<string, List<VectorRecord>> store = new(StringComparer.OrdinalIgnoreCase);

    public Task IndexAsync(string documentId, IEnumerable<VectorRecord> records, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(documentId);
        ArgumentNullException.ThrowIfNull(records);

        var list = records.ToList();

        lock (sync)
        {
            store[documentId] = list;
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<VectorRecord>> QueryAsync(string documentId, float[] queryEmbedding, int maxResults = 5, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(documentId);
        ArgumentNullException.ThrowIfNull(queryEmbedding);

        List<VectorRecord>? records;
        lock (sync)
        {
            if (!store.TryGetValue(documentId, out records))
            {
                return Task.FromResult<IReadOnlyList<VectorRecord>>(Array.Empty<VectorRecord>());
            }

            records = records.ToList();
        }

        if (queryEmbedding.Length == 0 || records.Count == 0 || maxResults <= 0)
        {
            return Task.FromResult<IReadOnlyList<VectorRecord>>(Array.Empty<VectorRecord>());
        }

        var scored = new List<(VectorRecord record, double score)>(records.Count);
        foreach (var record in records)
        {
            var score = CosineSimilarity(queryEmbedding, record.Embedding);
            scored.Add((record, score));
        }

        var result = scored
            .OrderByDescending(s => s.score)
            .ThenBy(s => s.record.StartOffset)
            .Take(maxResults)
            .Select(s => s.record)
            .ToList();

        return Task.FromResult<IReadOnlyList<VectorRecord>>(result);
    }

    private static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length == 0 || b.Length == 0 || a.Length != b.Length) return 0;

        double dot = 0;
        double normA = 0;
        double normB = 0;

        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        if (normA == 0 || normB == 0) return 0;

        return dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
    }
}
