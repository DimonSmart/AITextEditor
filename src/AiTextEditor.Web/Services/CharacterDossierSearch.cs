using AiTextEditor.Core.Model;

namespace AiTextEditor.Web.Services;

public static class CharacterDossierSearch
{
    public static IReadOnlyList<CharacterDossier> Filter(
        IEnumerable<CharacterDossier> dossiers,
        string? query)
    {
        ArgumentNullException.ThrowIfNull(dossiers);

        var normalizedQuery = query?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedQuery))
        {
            return dossiers.ToList();
        }

        return dossiers
            .Where(dossier => Matches(dossier, normalizedQuery))
            .ToList();
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
}
