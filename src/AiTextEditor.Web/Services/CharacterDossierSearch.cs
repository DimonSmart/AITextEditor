using AiTextEditor.Core.Model;

namespace AiTextEditor.Web.Services;

public static class CharacterDossierSearch
{
    public static IReadOnlyList<CharacterDossier> Filter(
        IEnumerable<CharacterDossier> dossiers,
        string? query,
        string? gender = null,
        bool onlyIncomplete = false)
    {
        ArgumentNullException.ThrowIfNull(dossiers);

        var normalizedQuery = query?.Trim();
        var normalizedGender = gender?.Trim();

        return dossiers
            .Where(dossier => string.IsNullOrWhiteSpace(normalizedQuery) || Matches(dossier, normalizedQuery))
            .Where(dossier => MatchesGender(dossier, normalizedGender))
            .Where(dossier => !onlyIncomplete || IsIncomplete(dossier))
            .ToList();
    }

    public static bool IsIncomplete(CharacterDossier dossier)
    {
        ArgumentNullException.ThrowIfNull(dossier);

        return string.IsNullOrWhiteSpace(dossier.Description)
            || dossier.Facts.Count == 0;
    }

    private static bool Matches(CharacterDossier dossier, string query)
    {
        return Contains(dossier.Name, query)
            || Contains(dossier.Description, query)
            || dossier.Aliases.Any(alias => Contains(alias, query))
            || dossier.AliasExamples.Any(item => Contains(item.Key, query) || Contains(item.Value, query))
            || dossier.Facts.Any(fact =>
                Contains(fact.Key, query)
                || Contains(fact.Value, query)
                || Contains(fact.Example, query));
    }

    private static bool Contains(string? value, string query)
    {
        return value?.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool MatchesGender(CharacterDossier dossier, string? gender)
    {
        return string.IsNullOrWhiteSpace(gender)
            || string.Equals(gender, "all", StringComparison.OrdinalIgnoreCase)
            || string.Equals(dossier.Gender, gender, StringComparison.OrdinalIgnoreCase);
    }
}
