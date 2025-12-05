using AiTextEditor.Lib.Interfaces;
using System.Security.Cryptography;
using System.Text;

namespace AiTextEditor.Lib.Services;

/// <summary>
/// Lightweight, deterministic embedding generator for local prototyping.
/// Produces fixed-size float vectors based on a hash of the input text.
/// </summary>
public class SimpleEmbeddingGenerator : IEmbeddingGenerator
{
    private readonly int dimensions;

    public SimpleEmbeddingGenerator(int dimensions = 64)
    {
        if (dimensions <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dimensions), "Dimensions must be positive.");
        }

        this.dimensions = dimensions;
    }

    public Task<float[]> GenerateAsync(string text, CancellationToken ct = default)
    {
        var vector = new float[dimensions];
        if (string.IsNullOrWhiteSpace(text))
        {
            return Task.FromResult(vector);
        }

        Span<byte> hashBytes = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(text), hashBytes);

        for (int i = 0; i < hashBytes.Length; i++)
        {
            int idx = i % dimensions;
            vector[idx] += hashBytes[i];
        }

        Normalize(vector);
        return Task.FromResult(vector);
    }

    private static void Normalize(float[] vector)
    {
        double norm = 0;
        for (int i = 0; i < vector.Length; i++)
        {
            norm += vector[i] * vector[i];
        }

        if (norm == 0) return;

        var scale = (float)(1.0 / Math.Sqrt(norm));
        for (int i = 0; i < vector.Length; i++)
        {
            vector[i] *= scale;
        }
    }
}
