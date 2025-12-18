using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiTextEditor.Lib.Model;

public sealed class SemanticPointer(int id, string? label = null)
{
    public int Id { get; } = id;
    public string? Label { get; } = string.IsNullOrWhiteSpace(label) ? null : label.Trim();

    [JsonIgnore]
    public Path Parsed => Path.TryParse(Label, out var p) ? p : default;

    public int Level => Parsed.Numbers?.Length ?? 0;
    public bool HasParagraph => Parsed.Paragraph is not null;

    public bool BelongsTo(SemanticPointer chapter)
    {
        var a = chapter.Parsed;
        var b = this.Parsed;

        if (a.Numbers is null || a.Numbers.Length == 0) return false;
        if (a.Paragraph is not null) return a.Equals(b); // параграф не контейнер

        if (b.Numbers is null) return false;
        if (a.Numbers.Length > b.Numbers.Length) return false;

        for (int i = 0; i < a.Numbers.Length; i++)
            if (a.Numbers[i] != b.Numbers[i]) return false;

        return true;
    }

    public bool IsCloseTo(SemanticPointer other, int tolerance)
    {
        if (tolerance < 0) return false;

        var a = this.Parsed;
        var b = other.Parsed;

        if (a.Numbers is null || b.Numbers is null) return false;
        if (a.Paragraph is null || b.Paragraph is null) return false;

        if (a.Numbers.Length != b.Numbers.Length) return false;
        for (int i = 0; i < a.Numbers.Length; i++)
            if (a.Numbers[i] != b.Numbers[i]) return false;

        var diff = Math.Abs(a.Paragraph.Value - b.Paragraph.Value);
        return diff <= tolerance;
    }

    public string ToCompactString()
        => !string.IsNullOrWhiteSpace(Label) ? $"{Id}:{Label}" : $"{Id}:p{Id}";

    public string Serialize() => JsonSerializer.Serialize(this, SerializationOptions);

    public static bool TryParse(string json, out SemanticPointer? pointer)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            pointer = null;
            return false;
        }

        try
        {
            pointer = JsonSerializer.Deserialize<SemanticPointer>(json, SerializationOptions);
            return pointer != null;
        }
        catch
        {
            pointer = null;
            return false;
        }
    }

    private static JsonSerializerOptions SerializationOptions { get; } = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true
    };

    public override string ToString() => Serialize();

    // Можно сделать private, если не нужно снаружи.
    public readonly record struct Path(int[]? Numbers, int? Paragraph)
    {
        public static bool TryParse(string? label, out Path path)
        {
            path = default;
            if (string.IsNullOrWhiteSpace(label)) return false;

            label = label.Trim();

            // Поддержка "1.2.3.p34" и "1.2.3p34"
            label = label.Replace(".p", "p").Replace(".P", "p");

            // "p10" (без секций) тоже поддержим
            if (label.Length >= 2 && (label[0] == 'p' || label[0] == 'P'))
            {
                if (!int.TryParse(label.AsSpan(1), out var pOnly)) return false;
                path = new Path(Array.Empty<int>(), pOnly);
                return true;
            }

            var parts = label.Split('p', 'P'); // максимум 2 части ожидается
            if (parts.Length > 2) return false;

            var left = parts[0];
            int? paragraph = null;

            if (parts.Length == 2)
            {
                if (string.IsNullOrWhiteSpace(parts[1])) return false;
                if (!int.TryParse(parts[1], out var p) || p < 0) return false;
                paragraph = p;
            }

            var numsStr = left.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (numsStr.Length == 0) return false;

            var nums = new int[numsStr.Length];
            for (int i = 0; i < numsStr.Length; i++)
            {
                if (!int.TryParse(numsStr[i], out var n) || n < 0) return false;
                nums[i] = n;
            }

            path = new Path(nums, paragraph);
            return true;
        }
    }
}
