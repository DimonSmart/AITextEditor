using AiTextEditor.Agent.CharacterBible;

namespace AiTextEditor.Agent.CharacterBible.Extraction;

internal static class CharacterBibleExtractionMapper
{
    private const int MaxObservedNameFormExampleLength = 160;
    private const int ContextWordsPerSide = 5;

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
        var observedNameFormExamples = NormalizeObservedNameForms(character.ObservedNameForms)
            .ToDictionary(
                observedNameForm => observedNameForm,
                observedNameForm => BuildObservedNameFormExample(observedNameForm, evidence),
                StringComparer.OrdinalIgnoreCase);
        var observedNameFormEvidence = observedNameFormExamples.Keys.ToDictionary(
            observedNameForm => observedNameForm,
            observedNameForm => FindEvidenceContaining(observedNameForm, evidence) ?? evidence[0],
            StringComparer.OrdinalIgnoreCase);

        return new CharacterBibleCharacterCandidate(
            character.Name?.Trim() ?? string.Empty,
            CharacterNameNormalizer.NormalizeGender(character.Gender),
            observedNameFormExamples,
            evidence)
        {
            ObservedNameFormEvidence = observedNameFormEvidence
        };
    }

    public static IReadOnlyList<string> NormalizeObservedNameForms(IReadOnlyList<string>? observedNameForms)
    {
        return (observedNameForms ?? [])
            .Where(observedNameForm => !string.IsNullOrWhiteSpace(observedNameForm))
            .Select(observedNameForm => observedNameForm.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(observedNameForm => observedNameForm, StringComparer.Ordinal)
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

    internal static string BuildObservedNameFormExample(
        string observedNameForm,
        IReadOnlyList<CharacterBibleCandidateEvidence> evidenceItems)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(observedNameForm);
        ArgumentNullException.ThrowIfNull(evidenceItems);

        var evidence = FindEvidenceContaining(observedNameForm, evidenceItems);
        if (evidence is null)
        {
            return TrimToMaxLength(evidenceItems.FirstOrDefault()?.Excerpt ?? string.Empty, MaxObservedNameFormExampleLength);
        }

        return BuildShortContext(evidence.Excerpt, observedNameForm);
    }

    private static CharacterBibleCandidateEvidence? FindEvidenceContaining(
        string observedNameForm,
        IReadOnlyList<CharacterBibleCandidateEvidence> evidenceItems)
    {
        return evidenceItems.FirstOrDefault(evidence =>
            !string.IsNullOrWhiteSpace(evidence.Excerpt) &&
            evidence.Excerpt.Contains(observedNameForm, StringComparison.Ordinal));
    }

    private static string BuildShortContext(string text, string observedNameForm)
    {
        var index = text.IndexOf(observedNameForm, StringComparison.Ordinal);
        if (index < 0)
        {
            return TrimToMaxLength(text.Trim(), MaxObservedNameFormExampleLength);
        }

        var before = text[..index];
        var afterStart = index + observedNameForm.Length;
        var after = afterStart >= text.Length ? string.Empty : text[afterStart..];
        var beforeWords = TakeLastWords(before, ContextWordsPerSide, out var trimmedStart);
        var afterWords = TakeFirstWords(after, ContextWordsPerSide, out var trimmedEnd);

        var result = string.Join(
            ' ',
            new[]
            {
                trimmedStart ? "..." : string.Empty,
                beforeWords,
                observedNameForm,
                afterWords,
                trimmedEnd ? "..." : string.Empty
            }.Where(part => !string.IsNullOrWhiteSpace(part)));

        return TrimToMaxLength(result, MaxObservedNameFormExampleLength);
    }

    private static string TakeLastWords(string text, int count, out bool trimmed)
    {
        var words = SplitWords(text);
        trimmed = words.Length > count;
        return string.Join(' ', words.TakeLast(count));
    }

    private static string TakeFirstWords(string text, int count, out bool trimmed)
    {
        var words = SplitWords(text);
        trimmed = words.Length > count;
        return string.Join(' ', words.Take(count));
    }

    private static string[] SplitWords(string text)
    {
        return text
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string TrimToMaxLength(string value, int maxLength)
    {
        var trimmed = value.Trim();
        if (trimmed.Length <= maxLength)
        {
            return trimmed;
        }

        return trimmed[..maxLength].TrimEnd();
    }
}
