using System.Text.Encodings.Web;
using System.Text.Json;

namespace AiTextEditor.Lib.Model;

/// <summary>
/// Represents a stable location inside a document with minimal context.
/// Serialized form uses JSON to include the heading title (if any), zero-based line index, and zero-based character offset.
/// </summary>
public class SemanticPointer
{
    public SemanticPointer(string? headingTitle, int lineIndex, int characterOffset)
    {
        HeadingTitle = string.IsNullOrWhiteSpace(headingTitle) ? null : headingTitle.Trim();
        LineIndex = lineIndex < 0 ? 0 : lineIndex;
        CharacterOffset = characterOffset < 0 ? 0 : characterOffset;
    }

    /// <summary>
    /// Title of the nearest heading or chapter that contains the target element. Can be null when the document lacks headings.
    /// </summary>
    public string? HeadingTitle { get; }

    /// <summary>
    /// Zero-based line number in the source document where the referenced element starts.
    /// </summary>
    public int LineIndex { get; }

    /// <summary>
    /// Zero-based character offset from the beginning of the document to the start of the referenced element.
    /// </summary>
    public int CharacterOffset { get; }

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
