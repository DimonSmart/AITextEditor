using AiTextEditor.Agent.CharacterBible.Extraction;
using AiTextEditor.Core.Model;

namespace AiTextEditor.Agent.CharacterBible.Resolution;

internal sealed class CharacterArchiveSearchService
{
    public const string ExactNameMatchReason = "exact_name";

    private const string AliasNameMatchReason = "alias_matches_existing_name";
    private const string NormalizedKeyMatchReason = "normalized_key";

    public IReadOnlyList<CharacterArchiveHit> Search(
        CharacterDossiers dossiers,
        CharacterArchiveSearchRequest request)
    {
        ArgumentNullException.ThrowIfNull(dossiers);
        ArgumentNullException.ThrowIfNull(request);

        var entries = dossiers.Characters
            .Select(ArchiveEntry.FromDossier)
            .Concat((dossiers.SuspectArchive ?? []).Select(ArchiveEntry.FromSuspect))
            .ToList();
        if (entries.Count == 0)
        {
            return [];
        }

        var exactNameMatches = FindExactNameMatches(entries, request);
        if (exactNameMatches.Count > 0)
        {
            return Limit(exactNameMatches, request.MaxResults);
        }

        var aliasNameMatches = FindAliasNameMatches(entries, request);
        if (aliasNameMatches.Count > 0)
        {
            return Limit(aliasNameMatches, request.MaxResults);
        }

        return Limit(FindNormalizedKeyMatches(entries, request), request.MaxResults);
    }

    public IReadOnlyList<CharacterArchiveSearchHit> SearchCharacters(
        CharacterDossiers dossiers,
        string query,
        int limit)
    {
        ArgumentNullException.ThrowIfNull(dossiers);

        var normalizedQuery = NormalizeSearchText(query);
        if (string.IsNullOrWhiteSpace(normalizedQuery) || limit <= 0)
        {
            return [];
        }

        return dossiers.Characters
            .Select(ArchiveEntry.FromDossier)
            .Concat((dossiers.SuspectArchive ?? []).Select(ArchiveEntry.FromSuspect))
            .Select(entry => (Entry: entry, Score: ScoreEntry(entry, normalizedQuery)))
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Entry.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Entry.EntryId, StringComparer.Ordinal)
            .Take(limit)
            .Select(item => item.Entry.ToSearchHit(item.Score))
            .ToArray();
    }

    public static CharacterArchiveSearchRequest CreateRequest(
        CharacterBibleCharacterCandidate candidate,
        int maxResults)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        return new CharacterArchiveSearchRequest(
            candidate.CanonicalName.Trim(),
            candidate.AliasExamples.Keys.ToArray(),
            CharacterNameNormalizer.NormalizeGender(candidate.Gender),
            candidate.Evidence,
            maxResults);
    }

    private static IReadOnlyList<CharacterArchiveHit> FindExactNameMatches(
        IReadOnlyList<ArchiveEntry> entries,
        CharacterArchiveSearchRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.CandidateName))
        {
            return [];
        }

        return entries
            .Where(entry => entry.MatchesName(request.CandidateName))
            .Select(entry => entry.ToHit([ExactNameMatchReason], Score: 100))
            .ToList();
    }

    private static IReadOnlyList<CharacterArchiveHit> FindAliasNameMatches(
        IReadOnlyList<ArchiveEntry> entries,
        CharacterArchiveSearchRequest request)
    {
        if (request.Aliases.Count == 0)
        {
            return [];
        }

        var matches = new Dictionary<string, CharacterArchiveHit>(StringComparer.Ordinal);
        foreach (var alias in request.Aliases)
        {
            if (string.IsNullOrWhiteSpace(alias))
            {
                continue;
            }

            foreach (var entry in entries)
            {
                if (entry.MatchesName(alias))
                {
                    matches[entry.EntryId] = entry.ToHit([AliasNameMatchReason], Score: 90);
                }
            }
        }

        return matches.Values.ToList();
    }

    private static IReadOnlyList<CharacterArchiveHit> FindNormalizedKeyMatches(
        IReadOnlyList<ArchiveEntry> entries,
        CharacterArchiveSearchRequest request)
    {
        var keyIndex = BuildKeyIndex(entries);
        var scores = new Dictionary<ArchiveEntry, int>();
        foreach (var variant in BuildCandidateKeyVariants(request))
        {
            if (!keyIndex.TryGetValue(variant.Key, out var matches))
            {
                continue;
            }

            foreach (var entry in matches)
            {
                scores[entry] = scores.TryGetValue(entry, out var score)
                    ? score + variant.Weight
                    : variant.Weight;
            }
        }

        if (scores.Count == 0)
        {
            return [];
        }

        var bestScore = scores.Values.Max();
        var threshold = ResolveMinMatchScore(request);
        if (bestScore < threshold)
        {
            return [];
        }

        return scores
            .Where(kvp => kvp.Value == bestScore)
            .Select(kvp => kvp.Key.ToHit([NormalizedKeyMatchReason], kvp.Value))
            .ToList();
    }

    private static Dictionary<string, HashSet<ArchiveEntry>> BuildKeyIndex(IReadOnlyList<ArchiveEntry> entries)
    {
        var keyIndex = new Dictionary<string, HashSet<ArchiveEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            foreach (var key in BuildEntryKeys(entry))
            {
                if (!keyIndex.TryGetValue(key, out var bucket))
                {
                    bucket = [];
                    keyIndex[key] = bucket;
                }

                bucket.Add(entry);
            }
        }

        return keyIndex;
    }

    private static IEnumerable<string> BuildEntryKeys(ArchiveEntry entry)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddKeys(entry.Name);
        foreach (var alias in entry.Aliases)
        {
            AddKeys(alias);
        }

        return keys;

        void AddKeys(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var baseKey = CharacterNameNormalizer.NormalizeKey(value);
            if (!string.IsNullOrWhiteSpace(baseKey))
            {
                keys.Add(baseKey);
            }

            if (CharacterNameNormalizer.TryGetPossessiveBase(value, out var possessiveBase))
            {
                var basePossessiveKey = CharacterNameNormalizer.NormalizeKey(possessiveBase);
                if (!string.IsNullOrWhiteSpace(basePossessiveKey))
                {
                    keys.Add(basePossessiveKey);
                }
            }
        }
    }

    private static List<KeyVariant> BuildCandidateKeyVariants(CharacterArchiveSearchRequest request)
    {
        var variants = new List<KeyVariant>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddVariants(request.CandidateName, 4);

        foreach (var alias in request.Aliases)
        {
            AddVariants(alias, 3);

            if (CharacterNameNormalizer.TryGetPossessiveBase(alias, out var baseForm))
            {
                AddVariants(baseForm, 2);
            }
        }

        return variants;

        void AddVariants(string? value, int weight)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var baseKey = CharacterNameNormalizer.NormalizeKey(value);
            if (!string.IsNullOrWhiteSpace(baseKey) && seen.Add(baseKey))
            {
                variants.Add(new KeyVariant(baseKey, weight));
            }
        }
    }

    private static int ResolveMinMatchScore(CharacterArchiveSearchRequest request)
    {
        var canonicalKey = CharacterNameNormalizer.NormalizeKey(request.CandidateName);
        return canonicalKey.Length <= 4 ? 6 : 4;
    }

    private static IReadOnlyList<CharacterArchiveHit> Limit(IReadOnlyList<CharacterArchiveHit> hits, int maxResults)
    {
        if (maxResults <= 0 || hits.Count <= maxResults)
        {
            return hits;
        }

        return hits.Take(maxResults).ToArray();
    }

    private sealed record KeyVariant(string Key, int Weight);

    private static double ScoreEntry(ArchiveEntry entry, string normalizedQuery)
    {
        var score = ScoreName(entry.Name, normalizedQuery, exact: 0.95, contains: 0.9);
        foreach (var alias in entry.Aliases)
        {
            score = Math.Max(score, ScoreName(alias, normalizedQuery, exact: 0.9, contains: 0.85));
        }

        if (!string.IsNullOrWhiteSpace(entry.ProfileSnippet))
        {
            var normalizedIdentity = NormalizeSearchText(entry.ProfileSnippet);
            var queryTerms = normalizedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var identityMatches = queryTerms.Count(term => normalizedIdentity.Contains(term, StringComparison.Ordinal));
            if (identityMatches > 0)
            {
                score = Math.Max(score, Math.Min(0.7, 0.25 + identityMatches * 0.1));
            }
        }

        return score;
    }

    private static double ScoreName(string value, string normalizedQuery, double exact, double contains)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        var normalizedValue = NormalizeSearchText(value);
        if (string.Equals(normalizedValue, normalizedQuery, StringComparison.Ordinal))
        {
            return exact;
        }

        if (normalizedQuery.Contains(normalizedValue, StringComparison.Ordinal))
        {
            return contains;
        }

        var normalizedKey = CharacterNameNormalizer.NormalizeKey(value);
        if (!string.IsNullOrWhiteSpace(normalizedKey)
            && normalizedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(term => string.Equals(term, normalizedKey, StringComparison.OrdinalIgnoreCase)))
        {
            return 0.8;
        }

        return 0;
    }

    private static string NormalizeSearchText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var character in value.Trim())
        {
            builder.Append(char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : ' ');
        }

        return string.Join(' ', builder.ToString().Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private sealed record ArchiveEntry(
        string EntryId,
        CharacterArchiveEntryKind EntryKind,
        string Name,
        IReadOnlyList<string> Aliases,
        string Gender,
        string ProfileSnippet)
    {
        public static ArchiveEntry FromDossier(CharacterDossier dossier)
        {
            return new ArchiveEntry(
                dossier.CharacterId,
                CharacterArchiveEntryKind.Character,
                dossier.Name,
                dossier.Aliases,
                string.IsNullOrWhiteSpace(dossier.Gender) ? "unknown" : dossier.Gender,
                BuildProfileSnippet(CharacterProfile.Normalize(dossier.Profile)));
        }

        public static ArchiveEntry FromSuspect(SuspectArchiveEntry suspect)
        {
            return new ArchiveEntry(
                suspect.CandidateId,
                CharacterArchiveEntryKind.Suspect,
                suspect.CanonicalName,
                suspect.Aliases,
                string.IsNullOrWhiteSpace(suspect.Gender) ? "unknown" : suspect.Gender,
                suspect.Reason);
        }

        public bool MatchesName(string name)
            => string.Equals(Name, name?.Trim(), StringComparison.OrdinalIgnoreCase);

        public CharacterArchiveHit ToHit(IReadOnlyList<string> matchReasons, int Score)
        {
            return new CharacterArchiveHit(
                EntryId,
                EntryKind,
                Name,
                Aliases,
                Gender,
                ProfileSnippet,
                matchReasons,
                Score);
        }

        public CharacterArchiveSearchHit ToSearchHit(double score)
        {
            return new CharacterArchiveSearchHit(
                EntryId,
                Name,
                Gender,
                Aliases,
                ProfileSnippet,
                Math.Round(score, 4));
        }

        private static string BuildProfileSnippet(CharacterProfile profile)
        {
            return string.Join(
                " ",
                new[]
                {
                    profile.Appearance,
                    profile.StatusAndCompetence,
                    profile.PsychologicalProfile,
                    profile.SpeechAndCommunication
                }.Where(value => !string.IsNullOrWhiteSpace(value)));
        }
    }
}
