using System.Globalization;
using System.Text;
using AiTextEditor.Agent.CharacterBible.Resolution;
using AiTextEditor.Agent.CharacterBible.VectorSearch;

namespace AiTextEditor.Agent.CharacterBible.Diagnostics;

internal static class LogValueFormatter
{
    public static string Quote(string? value)
    {
        if (value is null)
        {
            return "null";
        }

        return "\"" + value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal) + "\"";
    }

    public static string List(IEnumerable<string>? values, int maxItems = 10)
    {
        if (values is null)
        {
            return "null";
        }

        var normalized = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => Quote(value.Trim()))
            .Take(maxItems + 1)
            .ToArray();

        if (normalized.Length == 0)
        {
            return "[]";
        }

        var visible = normalized.Take(maxItems).ToList();
        if (normalized.Length > maxItems)
        {
            visible.Add("...");
        }

        return "[" + string.Join(", ", visible) + "]";
    }

    public static string List(IEnumerable<int>? values, int maxItems = 10)
    {
        if (values is null)
        {
            return "null";
        }

        return "[" + string.Join(", ", values.Take(maxItems)) + "]";
    }

    public static string Hits(IEnumerable<CharacterArchiveSearchHit>? hits, int maxItems = 10)
    {
        if (hits is null)
        {
            return "null";
        }

        return FormatHits(hits.Select((hit, index) => new HitValue(
            hit.Rank > 0 ? hit.Rank : index + 1,
            hit.CharacterId,
            hit.Name,
            hit.Score)), maxItems);
    }

    public static string VectorHits(IEnumerable<CharacterVectorSearchHit>? hits, int maxItems = 10)
    {
        if (hits is null)
        {
            return "null";
        }

        return FormatHits(hits.Select((hit, index) => new HitValue(
            index + 1,
            hit.Card.CharacterId,
            hit.Card.Name,
            hit.Score)), maxItems);
    }

    public static string ShortId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "empty";
        }

        var normalized = value.Trim();
        return normalized.Length <= 12 ? normalized : normalized[..12];
    }

    public static string ShortId(int? value)
        => value?.ToString(CultureInfo.InvariantCulture) ?? "empty";

    public static string NullableId(int? value)
        => value?.ToString(CultureInfo.InvariantCulture) ?? "null";

    public static string ShortText(string? value, int maxChars = 300)
    {
        if (value is null)
        {
            return "null";
        }

        var normalized = value
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();

        if (normalized.Length <= maxChars)
        {
            return normalized;
        }

        return normalized[..Math.Max(0, maxChars)] + "...";
    }

    public static string Score(double value)
    {
        return value.ToString("0.000", CultureInfo.InvariantCulture);
    }

    private static string FormatHits(IEnumerable<HitValue> hits, int maxItems)
    {
        var normalized = hits.Take(maxItems + 1).ToArray();
        if (normalized.Length == 0)
        {
            return "[]";
        }

        var builder = new StringBuilder("[");
        for (var index = 0; index < Math.Min(maxItems, normalized.Length); index++)
        {
            if (index > 0)
            {
                builder.Append(", ");
            }

            var hit = normalized[index];
            builder
                .Append(hit.Rank)
                .Append(':')
                .Append(hit.CharacterId)
                .Append(':')
                .Append(Quote(hit.Name))
                .Append(':')
                .Append(Score(hit.Score));
        }

        if (normalized.Length > maxItems)
        {
            builder.Append(", ...");
        }

        builder.Append(']');
        return builder.ToString();
    }

    private sealed record HitValue(
        int Rank,
        int CharacterId,
        string Name,
        double Score);
}
