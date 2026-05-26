using AiTextEditor.Agent.CharacterBible;
using AiTextEditor.Agent.CharacterBible.Extraction;
using AiTextEditor.Core.Model;
using AiTextEditor.Core.Services;

namespace AiTextEditor.Agent.CharacterBible.Resolution;

internal sealed class CharacterBibleResolver
{
    private const int MaxIncrementalNewCharacterLevel = 4;

    private readonly CharacterDossierService dossierService;
    private readonly CharacterBibleExtractionLimits limits;

    public CharacterBibleResolver(
        CharacterDossierService dossierService,
        CharacterBibleExtractionLimits limits)
    {
        this.dossierService = dossierService ?? throw new ArgumentNullException(nameof(dossierService));
        this.limits = limits ?? throw new ArgumentNullException(nameof(limits));
    }

    public CharacterBibleCommitPlan CreateCommitPlan(
        CharacterBibleWorkflowInput request,
        int paragraphCount,
        IReadOnlyList<CharacterBibleCharacterCandidate> candidates,
        IProgress<CharacterBibleWorkflowProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(candidates);

        var baseDossiers = dossierService.GetDossiers();
        if (candidates.Count == 0)
        {
            progress?.Report(new CharacterBibleWorkflowProgress(
                "resolve",
                "No candidates to resolve."));
            return new CharacterBibleCommitPlan(
                request,
                baseDossiers,
                false,
                paragraphCount,
                0,
                [],
                CharacterBibleModelResponseErrorStatistics.Empty);
        }

        var index = new DossierIndex(baseDossiers);
        var importanceAccumulator = new CharacterImportanceAccumulator();
        var createdCharacterIds = new HashSet<string>(StringComparer.Ordinal);
        var decisions = new List<CharacterBibleResolverDecision>(candidates.Count);
        var changed = false;
        var candidateNumber = 0;

        foreach (var candidate in candidates)
        {
            candidateNumber++;
            var hit = CharacterBibleExtractionMapper.ToCharacterExtractionCharacter(candidate);
            changed |= ApplyHitToIndex(index, hit, importanceAccumulator, createdCharacterIds, out var decision);
            decisions.Add(decision);
            progress?.Report(new CharacterBibleWorkflowProgress(
                "resolve",
                $"Resolved candidate {candidateNumber}/{candidates.Count}: {candidate.CanonicalName} -> {decision.Kind}."));
        }

        changed |= ApplyImportanceLevels(
            index,
            request.ChangedPointers is null,
            importanceAccumulator.Scores,
            createdCharacterIds);

        var projectedDossiers = changed
            ? index.ToDossiers(baseDossiers, limits.MaxCharacters)
            : baseDossiers;

        return new CharacterBibleCommitPlan(
            request,
            projectedDossiers,
            changed,
            paragraphCount,
            candidates.Count,
            decisions,
            CharacterBibleModelResponseErrorStatistics.Empty);
    }

    private static bool ApplyHitToIndex(
        DossierIndex index,
        CharacterExtractionCharacter hit,
        CharacterImportanceAccumulator importanceAccumulator,
        ISet<string> createdCharacterIds,
        out CharacterBibleResolverDecision decision)
    {
        var resolution = index.ResolveCandidate(hit);
        var canonicalName = hit.CanonicalName?.Trim() ?? string.Empty;

        if (resolution.Kind == CharacterBibleDecisionKind.Ambiguous)
        {
            decision = new CharacterBibleResolverDecision(
                canonicalName,
                CharacterBibleDecisionKind.Ambiguous,
                null,
                resolution.AmbiguousMatches.Select(profile => profile.Id).ToArray(),
                "Multiple existing dossiers matched the same name or alias key.");
            return false;
        }

        var profile = resolution.Profile
            ?? throw new InvalidOperationException("Resolved character profile is missing.");

        var changed = resolution.Created;
        importanceAccumulator.AddResolved(profile.Id);

        if (resolution.Created)
        {
            createdCharacterIds.Add(profile.Id);
        }

        var nameChanged = profile.RefineCanonicalName(hit, resolution.ExactNameMatch);
        changed |= nameChanged;

        var anyAliasChanged = profile.MergeAliases(hit, resolution.ExactNameMatch);
        changed |= anyAliasChanged;

        changed |= profile.SetGenderIfUnknown(hit.Gender);

        var profileChanged = profile.MergeProfile(CharacterBibleExtractionMapper.ToCharacterProfile(hit.Profile));
        changed |= profileChanged;

        if (resolution.Created || nameChanged || anyAliasChanged)
        {
            index.UpdateKeys(profile);
        }

        decision = new CharacterBibleResolverDecision(
            canonicalName,
            resolution.Created ? CharacterBibleDecisionKind.New : CharacterBibleDecisionKind.Existing,
            profile.Id,
            [],
            resolution.Created ? "No existing name or alias match was found." : "Matched by existing name or alias key.");

        return changed;
    }

    private static bool ApplyImportanceLevels(
        DossierIndex index,
        bool isFullGeneration,
        IReadOnlyDictionary<string, int> activityScores,
        IReadOnlySet<string> createdCharacterIds)
    {
        if (activityScores.Count == 0)
        {
            return false;
        }

        var maxScore = activityScores.Values.Max();
        var changed = false;

        foreach (var (characterId, score) in activityScores)
        {
            if (!isFullGeneration && !createdCharacterIds.Contains(characterId))
            {
                continue;
            }

            var level = CharacterImportance.ToLevel(score, maxScore);
            if (!isFullGeneration)
            {
                level = Math.Min(level, MaxIncrementalNewCharacterLevel);
            }

            changed |= index.SetImportanceLevelIfMissing(characterId, level);
        }

        return changed;
    }

    private sealed record KeyVariant(string Key, int Weight);

    private sealed class CharacterImportanceAccumulator
    {
        private readonly Dictionary<string, int> scores = new(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, int> Scores => scores;

        public void AddResolved(string characterId)
        {
            if (string.IsNullOrWhiteSpace(characterId))
            {
                return;
            }

            scores[characterId] = scores.TryGetValue(characterId, out var current)
                ? current + 1
                : 1;
        }
    }

    private sealed record DossierMatchResult(
        ProfileAccumulator? Profile,
        bool Created,
        bool ExactNameMatch,
        CharacterBibleDecisionKind Kind,
        IReadOnlyList<ProfileAccumulator> AmbiguousMatches)
    {
        public static DossierMatchResult Existing(ProfileAccumulator profile, bool exactNameMatch)
            => new(profile, false, exactNameMatch, CharacterBibleDecisionKind.Existing, []);

        public static DossierMatchResult New(ProfileAccumulator profile)
            => new(profile, true, true, CharacterBibleDecisionKind.New, []);

        public static DossierMatchResult Ambiguous(IReadOnlyList<ProfileAccumulator> matches)
            => new(null, false, false, CharacterBibleDecisionKind.Ambiguous, matches);
    }

    private sealed class DossierIndex
    {
        private readonly Dictionary<string, ProfileAccumulator> profiles = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, HashSet<ProfileAccumulator>> keyIndex = new(StringComparer.OrdinalIgnoreCase);

        public DossierIndex(CharacterDossiers dossiers)
        {
            foreach (var dossier in dossiers.Characters)
            {
                var accumulator = new ProfileAccumulator(dossier);
                profiles[accumulator.Id] = accumulator;
                IndexProfile(accumulator);
            }
        }

        public DossierMatchResult ResolveCandidate(CharacterExtractionCharacter candidate)
        {
            var (match, exactNameMatch, ambiguousMatches) = FindMatch(candidate);
            if (ambiguousMatches.Count > 0)
            {
                return DossierMatchResult.Ambiguous(ambiguousMatches);
            }

            if (match is not null)
            {
                return DossierMatchResult.Existing(match, exactNameMatch);
            }

            var accumulator = ProfileAccumulator.FromCandidate(candidate);
            profiles[accumulator.Id] = accumulator;
            IndexProfile(accumulator);
            return DossierMatchResult.New(accumulator);
        }

        public void UpdateKeys(ProfileAccumulator profile)
        {
            IndexProfile(profile);
        }

        public CharacterDossiers ToDossiers(CharacterDossiers baseDossiers, int? maxCharacters)
        {
            var items = profiles.Values
                .Select(profile => profile.ToDossier())
                .OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (maxCharacters.HasValue && maxCharacters.Value > 0 && items.Count > maxCharacters.Value)
            {
                items = items.Take(maxCharacters.Value).ToList();
            }

            return baseDossiers with { Characters = items };
        }

        public bool SetImportanceLevelIfMissing(string characterId, int level)
        {
            if (!profiles.TryGetValue(characterId, out var profile))
            {
                return false;
            }

            return profile.SetImportanceLevelIfMissing(level);
        }

        private void IndexProfile(ProfileAccumulator profile)
        {
            foreach (var key in BuildProfileKeys(profile))
            {
                if (!keyIndex.TryGetValue(key, out var bucket))
                {
                    bucket = new HashSet<ProfileAccumulator>();
                    keyIndex[key] = bucket;
                }

                bucket.Add(profile);
            }
        }

        private (ProfileAccumulator? Match, bool ExactNameMatch, IReadOnlyList<ProfileAccumulator> AmbiguousMatches) FindMatch(CharacterExtractionCharacter candidate)
        {
            var canonical = candidate.CanonicalName?.Trim();
            if (!string.IsNullOrWhiteSpace(canonical))
            {
                var exact = profiles.Values
                    .Where(profile => profile.MatchesName(canonical))
                    .ToList();

                if (exact.Count == 1)
                {
                    return (exact[0], true, []);
                }

                if (exact.Count > 1)
                {
                    return (null, false, exact);
                }
            }

            var aliasNameMatches = FindAliasNameMatches(candidate);
            if (aliasNameMatches.Count == 1)
            {
                return (aliasNameMatches[0], false, []);
            }

            if (aliasNameMatches.Count > 1)
            {
                return (null, false, aliasNameMatches);
            }

            var scores = new Dictionary<ProfileAccumulator, int>();
            foreach (var variant in BuildCandidateKeyVariants(candidate))
            {
                if (!keyIndex.TryGetValue(variant.Key, out var matches))
                {
                    continue;
                }

                foreach (var profile in matches)
                {
                    scores[profile] = scores.TryGetValue(profile, out var score)
                        ? score + variant.Weight
                        : variant.Weight;
                }
            }

            if (scores.Count == 0)
            {
                return (null, false, []);
            }

            var bestScore = scores.Values.Max();
            var threshold = ResolveMinMatchScore(candidate);
            if (bestScore < threshold)
            {
                return (null, false, []);
            }

            var best = scores
                .Where(kvp => kvp.Value == bestScore)
                .Select(kvp => kvp.Key)
                .ToList();

            return best.Count == 1 ? (best[0], false, []) : (null, false, best);
        }

        private IReadOnlyList<ProfileAccumulator> FindAliasNameMatches(CharacterExtractionCharacter candidate)
        {
            if (candidate.Aliases is not { Count: > 0 })
            {
                return [];
            }

            var matches = new HashSet<ProfileAccumulator>();
            foreach (var alias in candidate.Aliases)
            {
                if (string.IsNullOrWhiteSpace(alias.Form))
                {
                    continue;
                }

                foreach (var profile in profiles.Values)
                {
                    if (profile.MatchesName(alias.Form))
                    {
                        matches.Add(profile);
                    }
                }
            }

            return matches.ToList();
        }
    }

    private sealed class ProfileAccumulator
    {
        private readonly Dictionary<string, string> aliasExamples = new(StringComparer.OrdinalIgnoreCase);

        public string Id { get; }
        public string Name { get; private set; }
        public string Gender { get; private set; }
        public int? ImportanceLevel { get; private set; }
        public CharacterProfile Profile { get; private set; }
        public IReadOnlyDictionary<string, string> AliasExamples => aliasExamples;

        public ProfileAccumulator(CharacterDossier dossier)
        {
            Id = dossier.CharacterId;
            Name = dossier.Name;
            Gender = string.IsNullOrWhiteSpace(dossier.Gender) ? "unknown" : dossier.Gender;
            ImportanceLevel = dossier.ImportanceLevel;
            Profile = CharacterProfile.Normalize(dossier.Profile);

            MergeAliasExamples(dossier.AliasExamples);
        }

        private ProfileAccumulator(
            string name,
            string gender,
            IEnumerable<CharacterExtractionAlias>? aliases,
            CharacterProfile? profile)
        {
            Name = name.Trim();
            using var md5 = System.Security.Cryptography.MD5.Create();
            var hash = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(Name));
            Id = new Guid(hash).ToString("N");

            Gender = string.IsNullOrWhiteSpace(gender) ? "unknown" : gender.Trim();
            ImportanceLevel = null;
            Profile = CharacterProfile.Normalize(profile);

            if (aliases is not null)
            {
                foreach (var alias in aliases)
                {
                    AddAliasExample(alias.Form, alias.Example);
                }
            }
        }

        public static ProfileAccumulator FromCandidate(CharacterExtractionCharacter candidate)
        {
            var name = candidate.CanonicalName?.Trim() ?? string.Empty;
            return new ProfileAccumulator(
                name,
                candidate.Gender ?? "unknown",
                candidate.Aliases,
                CharacterBibleExtractionMapper.ToCharacterProfile(candidate.Profile));
        }

        public bool MatchesName(string name)
            => string.Equals(Name, name?.Trim(), StringComparison.OrdinalIgnoreCase);

        public bool RefineCanonicalName(CharacterExtractionCharacter candidate, bool exactNameMatch)
        {
            if (exactNameMatch)
            {
                return false;
            }

            var canonicalName = candidate.CanonicalName?.Trim();
            if (string.IsNullOrWhiteSpace(canonicalName))
            {
                return false;
            }

            if (string.Equals(Name, canonicalName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!IsGenderCompatible(candidate.Gender))
            {
                return false;
            }

            if (!TryFindExampleFor(Name, candidate.Aliases, out var currentNameExample))
            {
                return false;
            }

            AddAliasExample(Name, currentNameExample);
            Name = canonicalName;
            return true;
        }

        public bool SetGenderIfUnknown(string? gender)
        {
            if (string.IsNullOrWhiteSpace(gender))
            {
                return false;
            }

            if (!string.Equals(Gender, "unknown", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (string.Equals(gender, "unknown", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            Gender = gender.Trim();
            return true;
        }

        public bool MergeAliases(CharacterExtractionCharacter candidate, bool exactNameMatch)
        {
            var changed = false;

            if (candidate.Aliases is { Count: > 0 })
            {
                foreach (var alias in candidate.Aliases)
                {
                    changed |= AddAliasExample(alias.Form, alias.Example);
                }
            }

            if (exactNameMatch && !string.IsNullOrWhiteSpace(candidate.CanonicalName))
            {
                if (TryFindExampleFor(candidate.CanonicalName!, candidate.Aliases, out var example))
                {
                    changed |= AddAliasExample(candidate.CanonicalName!, example);
                }
            }

            return changed;
        }

        public bool SetImportanceLevelIfMissing(int level)
        {
            if (ImportanceLevel is not null)
            {
                return false;
            }

            var normalized = CharacterImportance.NormalizeLevel(level);
            if (normalized is null)
            {
                return false;
            }

            ImportanceLevel = normalized;
            return true;
        }

        public bool MergeProfile(CharacterProfile? candidateProfile)
        {
            var merged = CharacterProfile.MergeMissing(Profile, candidateProfile);
            if (CharacterProfile.HasSameContent(Profile, merged))
            {
                return false;
            }

            Profile = merged;
            return true;
        }

        public CharacterDossier ToDossier()
        {
            var normalizedAliasExamples = aliasExamples
                .Where(kvp => !string.IsNullOrWhiteSpace(kvp.Key) && !string.IsNullOrWhiteSpace(kvp.Value))
                .ToDictionary(kvp => kvp.Key.Trim(), kvp => kvp.Value.Trim(), StringComparer.OrdinalIgnoreCase);

            var aliases = normalizedAliasExamples.Keys
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(alias => alias, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new CharacterDossier(
                Id,
                Name,
                aliases,
                normalizedAliasExamples,
                string.IsNullOrWhiteSpace(Gender) ? "unknown" : Gender,
                ImportanceLevel,
                Profile);
        }

        private bool AddAliasExample(string alias, string example)
        {
            if (string.IsNullOrWhiteSpace(alias) || string.IsNullOrWhiteSpace(example))
            {
                return false;
            }

            var trimmedAlias = alias.Trim();
            if (trimmedAlias.Length == 0)
            {
                return false;
            }

            if (aliasExamples.ContainsKey(trimmedAlias))
            {
                return false;
            }

            aliasExamples[trimmedAlias] = example.Trim();
            return true;
        }

        private void MergeAliasExamples(IReadOnlyDictionary<string, string>? existing)
        {
            if (existing is null)
            {
                return;
            }

            foreach (var (alias, example) in existing)
            {
                AddAliasExample(alias, example);
            }
        }

        private static bool TryFindExampleFor(string name, IReadOnlyList<CharacterExtractionAlias>? aliases, out string example)
        {
            example = string.Empty;
            if (aliases is null)
            {
                return false;
            }

            var match = aliases.FirstOrDefault(alias => string.Equals(alias.Form, name, StringComparison.OrdinalIgnoreCase));
            if (match is null || string.IsNullOrWhiteSpace(match.Example))
            {
                return false;
            }

            example = match.Example;
            return true;
        }

        private bool IsGenderCompatible(string? candidateGender)
        {
            var normalizedCandidateGender = CharacterNameNormalizer.NormalizeGender(candidateGender);
            if (string.Equals(normalizedCandidateGender, "unknown", StringComparison.Ordinal))
            {
                return true;
            }

            if (string.Equals(Gender, "unknown", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return string.Equals(Gender, normalizedCandidateGender, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static List<KeyVariant> BuildCandidateKeyVariants(CharacterExtractionCharacter candidate)
    {
        var variants = new List<KeyVariant>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddVariants(candidate.CanonicalName, 4);

        if (candidate.Aliases is { Count: > 0 })
        {
            foreach (var alias in candidate.Aliases)
            {
                AddVariants(alias.Form, 3);

                if (CharacterNameNormalizer.TryGetPossessiveBase(alias.Form, out var baseForm))
                {
                    AddVariants(baseForm, 2);
                }
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

    private static int ResolveMinMatchScore(CharacterExtractionCharacter candidate)
    {
        var canonicalKey = CharacterNameNormalizer.NormalizeKey(candidate.CanonicalName ?? string.Empty);
        if (canonicalKey.Length <= 4)
        {
            return 6;
        }

        return 4;
    }

    private static IEnumerable<string> BuildProfileKeys(ProfileAccumulator profile)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddKeys(profile.Name);
        foreach (var alias in profile.AliasExamples.Keys)
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
}
