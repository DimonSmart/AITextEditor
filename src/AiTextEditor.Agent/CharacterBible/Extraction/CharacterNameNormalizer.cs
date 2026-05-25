using System.Text;

namespace AiTextEditor.Agent.CharacterBible.Extraction;

internal static class CharacterNameNormalizer
{
    public static bool TryGetPossessiveBase(string value, out string baseForm)
    {
        baseForm = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (trimmed.EndsWith("'s", StringComparison.OrdinalIgnoreCase) || trimmed.EndsWith("’s", StringComparison.OrdinalIgnoreCase))
        {
            baseForm = trimmed[..^2].Trim();
            return baseForm.Length > 0;
        }

        return false;
    }

    public static string NormalizeGender(string? gender)
    {
        if (string.IsNullOrWhiteSpace(gender))
        {
            return "unknown";
        }

        var normalizedGender = gender.Trim().ToLowerInvariant();
        return normalizedGender switch
        {
            "male" => "male",
            "female" => "female",
            _ => "unknown"
        };
    }

    public static string NormalizeKey(string value)
    {
        return NormalizeForComparison(value);
    }

    private static string NormalizeForComparison(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var lowered = value.Trim().ToLowerInvariant().Replace('ё', 'е');
        var builder = new StringBuilder(lowered.Length);
        var lastWasSpace = false;

        foreach (var ch in lowered)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
                lastWasSpace = false;
                continue;
            }

            if (char.IsWhiteSpace(ch) || ch == '-' || ch == '—' || ch == '–' || ch == '_')
            {
                if (!lastWasSpace)
                {
                    builder.Append(' ');
                    lastWasSpace = true;
                }
            }
        }

        return builder.ToString().Trim();
    }
}

