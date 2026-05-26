using System.Security.Cryptography;
using System.Text;

namespace AiTextEditor.Agent.CharacterBible.Extraction;

internal sealed class CandidatePostProcessor
{
    public IReadOnlyList<CharacterBibleCharacterCandidate> Process(
        IEnumerable<CharacterExtractionCharacter> extractedCharacters)
    {
        ArgumentNullException.ThrowIfNull(extractedCharacters);

        var candidates = new List<CharacterBibleCharacterCandidate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var extractedCharacter in extractedCharacters)
        {
            var normalized = CharacterBibleExtractionMapper.NormalizeHit(extractedCharacter);
            if (string.IsNullOrWhiteSpace(normalized.CanonicalName))
            {
                continue;
            }

            var candidate = CharacterBibleExtractionMapper.ToCandidate(normalized);
            if (candidate.Evidence.Count == 0)
            {
                continue;
            }

            var dedupeKey = BuildDedupeKey(candidate);
            if (!seen.Add(dedupeKey))
            {
                continue;
            }

            candidates.Add(candidate with { CandidateId = BuildCandidateId(candidate) });
        }

        return candidates;
    }

    private static string BuildDedupeKey(CharacterBibleCharacterCandidate candidate)
    {
        var evidenceKey = string.Join(
            "|",
            candidate.Evidence
                .Select(evidence => $"{evidence.Pointer}:{evidence.Excerpt}")
                .OrderBy(value => value, StringComparer.Ordinal));
        return $"{candidate.CanonicalName.Trim().ToUpperInvariant()}|{candidate.Gender}|{evidenceKey}";
    }

    private static string BuildCandidateId(CharacterBibleCharacterCandidate candidate)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(BuildDedupeKey(candidate)));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }
}
