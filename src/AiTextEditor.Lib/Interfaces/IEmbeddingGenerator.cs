namespace AiTextEditor.Lib.Interfaces;

public interface IEmbeddingGenerator
{
    Task<float[]> GenerateAsync(string text, CancellationToken ct = default);
}
