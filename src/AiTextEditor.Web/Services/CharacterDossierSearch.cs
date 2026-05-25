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

        var profile = CharacterProfile.Normalize(dossier.Profile);
        var emptySections = 5 - CharacterProfile.CountCompletedSections(profile);

        return string.IsNullOrWhiteSpace(profile.PsychologicalProfile)
            || emptySections >= 3;
    }

    private static bool Matches(CharacterDossier dossier, string query)
    {
        var profile = CharacterProfile.Normalize(dossier.Profile);

        return Contains(dossier.Name, query)
            || Contains(dossier.Description, query)
            || dossier.Aliases.Any(alias => Contains(alias, query))
            || dossier.AliasExamples.Any(item => Contains(item.Key, query) || Contains(item.Value, query))
            || Contains(profile.Appearance, query)
            || Contains(profile.BackgroundStatusEducation, query)
            || Contains(profile.PsychologicalProfile, query)
            || Contains(profile.SpeechAndCommunication, query)
            || (profile.KeyRoleBonds?.Any(bond =>
                Contains(bond.CharacterName, query)
                || Contains(bond.Role, query)
                || Contains(bond.Description, query)) == true)
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
