namespace AiTextEditor.Agent.CharacterBible.Extraction;

internal sealed class CandidatePostProcessor
{
    public IReadOnlyList<CharacterBibleCharacterCandidate> Process(
        IEnumerable<ExtractedLocalCharacter> extractedCharacters,
        IReadOnlyList<TextFragment> paragraphs,
        IProgress<CharacterBibleWorkflowProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(extractedCharacters);
        ArgumentNullException.ThrowIfNull(paragraphs);

        var paragraphsByPointer = paragraphs
            .Where(paragraph => !string.IsNullOrWhiteSpace(paragraph.Pointer))
            .ToDictionary(paragraph => paragraph.Pointer, StringComparer.Ordinal);
        var candidates = new List<CharacterBibleCharacterCandidate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var extractedCharacter in extractedCharacters)
        {
            if (string.IsNullOrWhiteSpace(extractedCharacter.Name))
            {
                progress?.Report(new CharacterBibleWorkflowProgress(
                    "extract",
                    "Rejected extracted character with empty name.",
                    IsError: true));
                continue;
            }

            var pointers = CharacterBibleExtractionMapper.NormalizePointers(extractedCharacter.Pointers);
            if (pointers.Count == 0)
            {
                progress?.Report(new CharacterBibleWorkflowProgress(
                    "extract",
                    $"Rejected extracted character '{extractedCharacter.Name.Trim()}' because it has no valid pointers.",
                    IsError: true));
                continue;
            }

            var missingPointer = pointers.FirstOrDefault(pointer => !paragraphsByPointer.ContainsKey(pointer));
            if (missingPointer is not null)
            {
                progress?.Report(new CharacterBibleWorkflowProgress(
                    "extract",
                    $"Rejected extracted character '{extractedCharacter.Name.Trim()}' because pointer '{missingPointer}' is not in the input batch.",
                    IsError: true));
                continue;
            }

            var exactRepeatKey = BuildExactRepeatKey(extractedCharacter);
            if (!seen.Add(exactRepeatKey))
            {
                continue;
            }

            var candidate = CharacterBibleExtractionMapper.ToCandidate(extractedCharacter, paragraphsByPointer);
            candidates.Add(candidate);
        }

        return candidates;
    }

    private static string BuildExactRepeatKey(ExtractedLocalCharacter character)
    {
        var observedNameForms = string.Join(
            "|",
            CharacterBibleExtractionMapper.NormalizeObservedNameForms(character.ObservedNameForms)
                .Order(StringComparer.Ordinal));
        var pointers = string.Join(
            "|",
            CharacterBibleExtractionMapper.NormalizePointers(character.Pointers)
                .Order(StringComparer.Ordinal));

        return string.Join(
            "\u001f",
            Normalize(character.Name),
            Normalize(character.Gender),
            observedNameForms,
            pointers);
    }

    private static string Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToUpperInvariant();
}
