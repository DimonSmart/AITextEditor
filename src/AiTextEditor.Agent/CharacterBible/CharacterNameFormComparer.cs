namespace AiTextEditor.Agent.CharacterBible;

internal sealed class CharacterNameFormComparer : IEqualityComparer<string>
{
    public static readonly CharacterNameFormComparer Instance = new();

    private CharacterNameFormComparer()
    {
    }

    public bool Equals(string? x, string? y)
        => string.Equals(NormalizeKey(x), NormalizeKey(y), StringComparison.Ordinal);

    public int GetHashCode(string obj)
        => NormalizeKey(obj).GetHashCode(StringComparison.Ordinal);

    public static string NormalizeKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value
            .Trim()
            .Replace('ё', 'е')
            .Replace('Ё', 'Е')
            .ToUpperInvariant();
    }
}
