using AiTextEditor.Agent.CharacterBible;
using AiTextEditor.Core.Model;

namespace AiTextEditor.Agent.CharacterBible.Extraction;

internal static class CharacterBibleExtractionMapper
{
    public static CharacterExtractionCharacter NormalizeHit(CharacterExtractionCharacter hit)
    {
        var canonical = (hit.CanonicalName ?? string.Empty).Trim();
        var profile = ToExtractionProfile(ToCharacterProfile(hit.Profile));

        var normalizedAliases = (hit.Aliases ?? [])
            .Where(alias => alias is not null && !string.IsNullOrWhiteSpace(alias.Form) && !string.IsNullOrWhiteSpace(alias.Example))
            .Select(alias => new CharacterExtractionAlias(alias.Form.Trim(), alias.Example.Trim()))
            .DistinctBy(alias => alias.Form, StringComparer.OrdinalIgnoreCase)
            .OrderBy(alias => alias.Form, StringComparer.OrdinalIgnoreCase)
            .ToList();

        normalizedAliases = AddPossessiveBaseAliases(normalizedAliases);

        return hit with
        {
            CanonicalName = canonical,
            Aliases = normalizedAliases,
            Gender = CharacterNameNormalizer.NormalizeGender(hit.Gender),
            Profile = profile
        };
    }

    public static CharacterBibleCharacterCandidate ToCandidate(CharacterExtractionCharacter hit)
    {
        var aliasExamples = (hit.Aliases ?? [])
            .Where(alias => !string.IsNullOrWhiteSpace(alias.Form) && !string.IsNullOrWhiteSpace(alias.Example))
            .ToDictionary(
                alias => alias.Form.Trim(),
                alias => alias.Example.Trim(),
                StringComparer.OrdinalIgnoreCase);

        return new CharacterBibleCharacterCandidate(
            hit.CanonicalName?.Trim() ?? string.Empty,
            CharacterNameNormalizer.NormalizeGender(hit.Gender),
            aliasExamples,
            ToCharacterProfile(hit.Profile));
    }

    public static CharacterExtractionCharacter ToCharacterExtractionCharacter(CharacterBibleCharacterCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        var aliases = candidate.AliasExamples
            .Select(alias => new CharacterExtractionAlias(alias.Key, alias.Value))
            .ToList();

        return NormalizeHit(new CharacterExtractionCharacter(
            candidate.CanonicalName,
            candidate.Gender,
            aliases,
            ToExtractionProfile(candidate.Profile)));
    }

    public static CharacterProfile ToCharacterProfile(CharacterExtractionProfile? profile)
    {
        if (profile is null)
        {
            return CharacterProfile.Empty;
        }

        return CharacterProfile.Normalize(new CharacterProfile(
            profile.Appearance ?? string.Empty,
            profile.StatusAndCompetence ?? string.Empty,
            profile.PsychologicalProfile ?? string.Empty,
            profile.SpeechAndCommunication ?? string.Empty));
    }

    private static CharacterExtractionProfile ToExtractionProfile(CharacterProfile? profile)
    {
        var normalized = CharacterProfile.Normalize(profile);
        return new CharacterExtractionProfile(
            normalized.Appearance,
            normalized.StatusAndCompetence,
            normalized.PsychologicalProfile,
            normalized.SpeechAndCommunication);
    }

    private static List<CharacterExtractionAlias> AddPossessiveBaseAliases(List<CharacterExtractionAlias> aliases)
    {
        if (aliases.Count == 0)
        {
            return aliases;
        }

        var seen = new HashSet<string>(aliases.Select(alias => alias.Form), StringComparer.OrdinalIgnoreCase);
        var expanded = new List<CharacterExtractionAlias>(aliases);

        foreach (var alias in aliases)
        {
            if (!CharacterNameNormalizer.TryGetPossessiveBase(alias.Form, out var baseForm))
            {
                continue;
            }

            if (seen.Add(baseForm))
            {
                expanded.Add(new CharacterExtractionAlias(baseForm, alias.Example));
            }
        }

        return expanded
            .OrderBy(alias => alias.Form, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
