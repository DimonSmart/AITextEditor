using AiTextEditor.Lib.Interfaces;
using AiTextEditor.Lib.Model;

namespace AiTextEditor.Lib.Services;

public class InMemoryVectorStore : IVectorStore
{
    private readonly Dictionary<string, List<Chunk>> _store = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();

    public Task IndexAsync(string documentId, IEnumerable<Chunk> chunks, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(documentId);
        ArgumentNullException.ThrowIfNull(chunks);

        var chunkList = chunks.ToList();

        lock (_sync)
        {
            _store[documentId] = chunkList;
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Chunk>> QueryAsync(string documentId, string query, int maxResults = 5, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(documentId);
        ArgumentNullException.ThrowIfNull(query);

        if (maxResults <= 0)
        {
            return Task.FromResult<IReadOnlyList<Chunk>>(Array.Empty<Chunk>());
        }

        List<Chunk>? chunks;
        lock (_sync)
        {
            if (!_store.TryGetValue(documentId, out chunks))
            {
                return Task.FromResult<IReadOnlyList<Chunk>>(Array.Empty<Chunk>());
            }

            chunks = chunks.ToList(); // return a copy to protect internal state
        }

        var result = chunks.Take(maxResults).ToList();
        return Task.FromResult<IReadOnlyList<Chunk>>(result);
    }
}
