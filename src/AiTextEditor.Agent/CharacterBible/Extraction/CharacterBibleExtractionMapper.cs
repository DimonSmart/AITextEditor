using AiTextEditor.Agent.CharacterBible;
using AiTextEditor.Core.Model;

namespace AiTextEditor.Agent.CharacterBible.Extraction;

internal static class CharacterBibleExtractionMapper
{
    public static CharacterExtractionCharacter NormalizeHit(CharacterExtractionCharacter hit)
    {
        var canonical = (hit.CanonicalName ?? string.Empty).Trim();
        var evidence = NormalizeEvidence(hit.Evidence);

        var normalizedAliases = (hit.Aliases ?? [])
            .Where(alias => alias is not null && !string.IsNullOrWhiteSpace(alias.Form))
            .Select(alias => new CharacterExtractionAlias(alias.Form.Trim(), NormalizeEvidence(alias.Evidence)))
            .Where(alias => alias.Evidence is not null)
            .DistinctBy(alias => alias.Form, StringComparer.OrdinalIgnoreCase)
            .OrderBy(alias => alias.Form, StringComparer.OrdinalIgnoreCase)
            .ToList();

        normalizedAliases = AddPossessiveBaseAliases(normalizedAliases);

        return hit with
        {
            CanonicalName = canonical,
            Aliases = normalizedAliases,
            Gender = CharacterNameNormalizer.NormalizeGender(hit.Gender),
            Evidence = evidence
        };
    }

    public static CharacterBibleCharacterCandidate ToCandidate(CharacterExtractionCharacter hit)
    {
        var aliasExamples = (hit.Aliases ?? [])
            .Where(alias => !string.IsNullOrWhiteSpace(alias.Form) && !string.IsNullOrWhiteSpace(alias.Evidence?.Excerpt))
            .ToDictionary(
                alias => alias.Form.Trim(),
                alias => alias.Evidence!.Excerpt!.Trim(),
                StringComparer.OrdinalIgnoreCase);
        var aliasEvidence = (hit.Aliases ?? [])
            .Where(alias => !string.IsNullOrWhiteSpace(alias.Form)
                            && !string.IsNullOrWhiteSpace(alias.Evidence?.Pointer)
                            && !string.IsNullOrWhiteSpace(alias.Evidence?.Excerpt))
            .ToDictionary(
                alias => alias.Form.Trim(),
                alias => new CharacterBibleCandidateEvidence(alias.Evidence!.Pointer!.Trim(), alias.Evidence.Excerpt!.Trim()),
                StringComparer.OrdinalIgnoreCase);

        return new CharacterBibleCharacterCandidate(
            hit.CanonicalName?.Trim() ?? string.Empty,
            CharacterNameNormalizer.NormalizeGender(hit.Gender),
            aliasExamples,
            NormalizeEvidence(hit.Evidence)
                .Select(evidence => new CharacterBibleCandidateEvidence(evidence.Pointer!, evidence.Excerpt!))
                .ToList())
        {
            AliasEvidence = aliasEvidence
        };
    }

    public static CharacterExtractionCharacter ToCharacterExtractionCharacter(CharacterBibleCharacterCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        var aliases = candidate.AliasExamples
            .Select(alias => new CharacterExtractionAlias(alias.Key, ToExtractionEvidence(candidate, alias.Key, alias.Value)))
            .ToList();

        return NormalizeHit(new CharacterExtractionCharacter(
            candidate.CanonicalName,
            candidate.Gender,
            aliases,
            candidate.Evidence
                .Select(evidence => new CharacterExtractionEvidence(evidence.Pointer, evidence.Excerpt))
                .ToList()));
    }

    private static CharacterExtractionEvidence? NormalizeEvidence(CharacterExtractionEvidence? evidence)
    {
        if (evidence is null
            || string.IsNullOrWhiteSpace(evidence.Pointer)
            || string.IsNullOrWhiteSpace(evidence.Excerpt))
        {
            return null;
        }

        return new CharacterExtractionEvidence(evidence.Pointer.Trim(), evidence.Excerpt.Trim());
    }

    private static List<CharacterExtractionEvidence> NormalizeEvidence(IEnumerable<CharacterExtractionEvidence>? evidence)
    {
        return (evidence ?? [])
            .Select(NormalizeEvidence)
            .Where(item => item is not null)
            .Select(item => item!)
            .DistinctBy(item => $"{item.Pointer}\u001f{item.Excerpt}", StringComparer.Ordinal)
            .ToList();
    }

    private static CharacterExtractionEvidence ToExtractionEvidence(
        CharacterBibleCharacterCandidate candidate,
        string alias,
        string excerpt)
    {
        var pointer = candidate.AliasEvidence.TryGetValue(alias, out var aliasEvidence)
            ? aliasEvidence.Pointer
            : candidate.Evidence.FirstOrDefault()?.Pointer ?? string.Empty;
        return new CharacterExtractionEvidence(pointer, excerpt);
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
                expanded.Add(new CharacterExtractionAlias(baseForm, alias.Evidence));
            }
        }

        return expanded
            .OrderBy(alias => alias.Form, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
