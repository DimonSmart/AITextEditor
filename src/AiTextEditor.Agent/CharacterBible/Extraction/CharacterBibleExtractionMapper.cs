using AiTextEditor.Agent.CharacterBible;

namespace AiTextEditor.Agent.CharacterBible.Extraction;

internal static class CharacterBibleExtractionMapper
{
    public static CharacterBibleCharacterCandidate ToCandidate(
        ExtractedLocalCharacter character,
        IReadOnlyDictionary<string, TextFragment> paragraphsByPointer)
    {
        ArgumentNullException.ThrowIfNull(character);
        ArgumentNullException.ThrowIfNull(paragraphsByPointer);

        var pointers = NormalizePointers(character.Pointers);
        var evidence = pointers
            .Select(pointer => new CharacterBibleCandidateEvidence(pointer, paragraphsByPointer[pointer].Text.Trim()))
            .ToArray();
        var firstEvidence = evidence[0];
        var aliases = NormalizeAliases(character.Aliases)
            .ToDictionary(
                alias => alias,
                _ => firstEvidence.Excerpt,
                StringComparer.OrdinalIgnoreCase);
        var aliasEvidence = aliases.Keys.ToDictionary(
            alias => alias,
            _ => firstEvidence,
            StringComparer.OrdinalIgnoreCase);

        return new CharacterBibleCharacterCandidate(
            character.Name?.Trim() ?? string.Empty,
            CharacterNameNormalizer.NormalizeGender(character.Gender),
            aliases,
            evidence)
        {
            AliasEvidence = aliasEvidence
        };
    }

    public static IReadOnlyList<string> NormalizeAliases(IReadOnlyList<string>? aliases)
    {
        return (aliases ?? [])
            .Where(alias => !string.IsNullOrWhiteSpace(alias))
            .Select(alias => alias.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(alias => alias, StringComparer.Ordinal)
            .ToArray();
    }

    public static IReadOnlyList<string> NormalizePointers(IReadOnlyList<string>? pointers)
    {
        return (pointers ?? [])
            .Where(pointer => !string.IsNullOrWhiteSpace(pointer))
            .Select(pointer => pointer.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(pointer => pointer, StringComparer.Ordinal)
            .ToArray();
    }
}
