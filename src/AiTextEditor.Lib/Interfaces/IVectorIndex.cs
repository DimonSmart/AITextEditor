using AiTextEditor.Lib.Model.Indexing;

namespace AiTextEditor.Lib.Interfaces;

/// <summary>
/// Pluggable vector index that stores embeddings and can return the closest records.
/// </summary>
public interface IVectorIndex
{
    Task IndexAsync(string documentId, IEnumerable<VectorRecord> records, CancellationToken ct = default);

    Task<IReadOnlyList<VectorRecord>> QueryAsync(string documentId, float[] queryEmbedding, int maxResults = 5, CancellationToken ct = default);
}
