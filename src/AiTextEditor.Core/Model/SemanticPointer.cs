using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiTextEditor.Core.Model;

public sealed class SemanticPointer
{
    public string Label { get; }

    public SemanticPointer(string label)
    {
        Label = NormalizeLabel(label);
        if (string.IsNullOrWhiteSpace(Label))
        {
            throw new ArgumentException("Semantic pointer label cannot be empty.", nameof(label));
        }
    }

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

    public string ToCompactString() => Label;

    public string Serialize() => JsonSerializer.Serialize(this, SerializationOptions);

    public static bool TryParse(string raw, out SemanticPointer? pointer)
    {
        pointer = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var trimmed = raw.Trim();
        string? label = null;

        if (trimmed.StartsWith("{", StringComparison.Ordinal))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return false;
                }

                if (TryReadLabel(doc.RootElement, out var jsonLabel))
                {
                    label = jsonLabel;
                }
            }
            catch
            {
                return false;
            }
        }
        else
        {
            label = trimmed;
            var colonIndex = trimmed.IndexOf(':');
            if (colonIndex > 0)
            {
                var prefix = trimmed[..colonIndex];
                var suffix = trimmed[(colonIndex + 1)..];
                if (IsDigits(prefix) && !string.IsNullOrWhiteSpace(suffix))
                {
                    label = suffix;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(label))
        {
            return false;
        }

        label = NormalizeLabel(label);
        if (!Path.TryParse(label, out _))
        {
            return false;
        }

        pointer = new SemanticPointer(label);
        return true;
    }

    private static bool IsDigits(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        foreach (var ch in value)
        {
            if (!char.IsDigit(ch))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryReadLabel(JsonElement root, out string? label)
    {
        label = null;
        foreach (var property in root.EnumerateObject())
        {
            if (!string.Equals(property.Name, "label", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (property.Value.ValueKind == JsonValueKind.String)
            {
                label = property.Value.GetString();
            }

            return !string.IsNullOrWhiteSpace(label);
        }

        return false;
    }

    private static string NormalizeLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return string.Empty;
        }

        var normalized = label.Trim();
        normalized = normalized.Replace('P', 'p');

        var pIndex = normalized.IndexOf('p');
        if (pIndex > 0 && normalized[pIndex - 1] != '.')
        {
            normalized = normalized.Insert(pIndex, ".");
        }

        return normalized;
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
