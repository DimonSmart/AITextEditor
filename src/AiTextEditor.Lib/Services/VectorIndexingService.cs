using AiTextEditor.Lib.Interfaces;
using AiTextEditor.Lib.Model;
using AiTextEditor.Lib.Model.Indexing;

namespace AiTextEditor.Lib.Services;

/// <summary>
/// Coordinates embedding generation and storage in a vector index.
/// </summary>
public class VectorIndexingService
{
    private readonly IEmbeddingGenerator embeddingGenerator;
    private readonly IVectorIndex vectorIndex;

    public VectorIndexingService(IEmbeddingGenerator embeddingGenerator, IVectorIndex vectorIndex)
    {
        this.embeddingGenerator = embeddingGenerator ?? throw new ArgumentNullException(nameof(embeddingGenerator));
        this.vectorIndex = vectorIndex ?? throw new ArgumentNullException(nameof(vectorIndex));
    }

    public async Task<IReadOnlyList<VectorRecord>> IndexAsync(Document document, TextIndex textIndex, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(textIndex);

        var records = new List<VectorRecord>(textIndex.Entries.Count);
        foreach (var entry in textIndex.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var embedding = await embeddingGenerator.GenerateAsync(entry.Text, ct);
            records.Add(new VectorRecord
            {
                DocumentId = document.Id,
                BlockId = entry.BlockId,
                StructuralPath = entry.StructuralPath,
                Text = entry.Text,
                Embedding = embedding,
                StartOffset = entry.StartOffset,
                EndOffset = entry.EndOffset,
                StartLine = entry.StartLine,
                EndLine = entry.EndLine
            });
        }

        await vectorIndex.IndexAsync(document.Id, records, ct);
        return records;
    }

    public async Task<IReadOnlyList<VectorRecord>> QueryAsync(string documentId, string query, int maxResults = 5, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(documentId);
        ArgumentNullException.ThrowIfNull(query);

        var embedding = await embeddingGenerator.GenerateAsync(query, ct);
        return await vectorIndex.QueryAsync(documentId, embedding, maxResults, ct);
    }
}
