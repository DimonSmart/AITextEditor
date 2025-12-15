using System.Text.Encodings.Web;
using System.Text.Json;

namespace AiTextEditor.Lib.Model;

/// <summary>
/// Represents a stable location inside a document with minimal context.
/// Serialized form uses JSON containing a stable Id and a human-readable Label.
/// </summary>
public class SemanticPointer
{
    public SemanticPointer(int id, string? label = null)
    {
        Id = id;
        Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim();
    }

    /// <summary>
    /// Stable numeric identifier of the target element inside the document.
    /// </summary>
    public int Id { get; }

    /// <summary>
    /// Optional human-readable pointer (e.g., section/paragraph numbering).
    /// </summary>
    public string? Label { get; }

    /// <summary>
    /// Returns a compact string representation suitable for LLM context (e.g. "25:1.1.1.p22").
    /// </summary>
    public string ToCompactString()
    {
        return !string.IsNullOrWhiteSpace(Label)
            ? $"{Id}:{Label}"
            : $"{Id}:p{Id}";
    }

    public string Serialize()
    {
        return JsonSerializer.Serialize(this, SerializationOptions);
    }
    private static JsonSerializerOptions SerializationOptions { get; } = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public override string ToString()
    {
        return Serialize();
    }
}
