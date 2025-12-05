using AiTextEditor.Lib.Model;

namespace AiTextEditor.Lib.Interfaces;

public interface IVectorStore
{
    Task IndexAsync(string documentId, IEnumerable<Chunk> chunks, CancellationToken ct = default);

    Task<IReadOnlyList<Chunk>> QueryAsync(string documentId, string query, int maxResults = 5, CancellationToken ct = default);
}
