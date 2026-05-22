using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiTextEditor.Core.Model;
using AiTextEditor.Core.Services;
using AiTextEditor.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace AiTextEditor.Agent;

public sealed class CharacterDossiersGenerator
{
    private readonly IDocumentContext documentContext;
    private readonly CharacterDossierService dossierService;
    private readonly CursorAgentLimits limits;
    private readonly ILogger<CharacterDossiersGenerator> logger;
    private readonly ICharacterExtractionModelClient characterExtractionModelClient;

    public CharacterDossiersGenerator(
        IDocumentContext documentContext,
        CharacterDossierService dossierService,
        CursorAgentLimits limits,
        ILogger<CharacterDossiersGenerator> logger,
        ICharacterExtractionModelClient characterExtractionModelClient)
    {
        this.documentContext = documentContext ?? throw new ArgumentNullException(nameof(documentContext));
        this.dossierService = dossierService ?? throw new ArgumentNullException(nameof(dossierService));
        this.limits = limits ?? throw new ArgumentNullException(nameof(limits));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.characterExtractionModelClient = characterExtractionModelClient ?? throw new ArgumentNullException(nameof(characterExtractionModelClient));
    }

    internal IReadOnlyList<TextFragment> CollectParagraphs(IReadOnlyCollection<string>? changedPointers)
    {
        var pointerSet = changedPointers?
            .Where(pointer => !string.IsNullOrWhiteSpace(pointer))
            .Select(pointer => pointer.Trim())
            .ToHashSet(StringComparer.Ordinal);

        if (pointerSet is { Count: > 0 })
        {
            return CollectChangedParagraphs(pointerSet);
        }

        if (changedPointers is not null)
        {
            return [];
        }

        return CollectAllParagraphs();
    }

    internal async Task<IReadOnlyList<CharacterBibleCharacterCandidate>> ExtractCandidatesAsync(
        IReadOnlyList<TextFragment> paragraphs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paragraphs);
        cancellationToken.ThrowIfCancellationRequested();

        if (paragraphs.Count == 0)
        {
            return [];
        }

        var candidates = new List<CharacterBibleCharacterCandidate>();
        foreach (var batch in SplitParagraphs(paragraphs.Select(p => (p.Pointer, p.Text)).ToList()))
        {
            var hits = await ExtractCharactersWithModelAsync(batch, cancellationToken);
            candidates.AddRange(hits.Select(ToCandidate));
        }

        return candidates;
    }

    internal CharacterBibleCommitPlan CreateCommitPlan(
        CharacterBibleWorkflowInput request,
        int paragraphCount,
        IReadOnlyList<CharacterBibleCharacterCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(candidates);

        var baseDossiers = dossierService.GetDossiers();
        if (candidates.Count == 0)
        {
            return new CharacterBibleCommitPlan(request, baseDossiers, false, paragraphCount, 0, []);
        }

        var index = new DossierIndex(baseDossiers);
        var decisions = new List<CharacterBibleResolverDecision>(candidates.Count);
        var changed = false;

        foreach (var candidate in candidates)
        {
            var hit = ToCharacterExtractionCharacter(candidate);
            changed |= ApplyHitToIndex(index, hit, out var decision);
            decisions.Add(decision);
        }

        var projectedDossiers = changed
            ? index.ToDossiers(baseDossiers, limits.CharacterDossiersMaxCharacters)
            : baseDossiers;

        return new CharacterBibleCommitPlan(
            request,
            projectedDossiers,
            changed,
            paragraphCount,
            candidates.Count,
            decisions);
    }

    internal CharacterDossiers CommitPlan(CharacterBibleCommitPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (plan.Changed)
        {
            dossierService.ReplaceDossiers(plan.ProjectedDossiers.Characters);
        }

        return dossierService.GetDossiers();
    }

    internal CharacterDossiers GetCurrentDossiers() => dossierService.GetDossiers();

    private static bool ApplyHitToIndex(
        DossierIndex index,
        CharacterExtractionCharacter hit,
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

        var anyAliasChanged = profile.MergeAliases(hit, resolution.ExactNameMatch);
        changed |= anyAliasChanged;

        changed |= profile.SetGenderIfUnknown(hit.Gender);

        var descriptionChanged = profile.SetDescriptionIfEmpty(hit.Description);
        changed |= descriptionChanged;

        if (resolution.Created || anyAliasChanged || descriptionChanged)
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

    private IReadOnlyList<TextFragment> CollectAllParagraphs()
    {
        var cursor = new FullScanCursorStream(
            documentContext.Document,
            limits.MaxElements,
            limits.MaxBytes,
            null,
            includeHeadings: false,
            logger);

        var paragraphs = new List<TextFragment>();
        while (true)
        {
            var portion = cursor.NextPortion();
            if (portion.Items.Count == 0 && !portion.HasMore)
            {
                break;
            }

            foreach (var item in portion.Items)
            {
                if (item.Type == LinearItemType.Heading)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(item.Markdown))
                {
                    continue;
                }

                paragraphs.Add(new TextFragment(item.Pointer.ToCompactString(), item.Markdown));
            }

            if (!portion.HasMore)
            {
                break;
            }
        }

        return paragraphs;
    }

    private IReadOnlyList<TextFragment> CollectChangedParagraphs(IReadOnlySet<string> pointerSet)
    {
        var lookup = documentContext.Document.Items
            .ToDictionary(item => item.Pointer.ToCompactString(), item => item, StringComparer.Ordinal);

        var paragraphs = new List<TextFragment>(pointerSet.Count);
        foreach (var pointer in pointerSet)
        {
            if (!lookup.TryGetValue(pointer, out var item))
            {
                logger.LogWarning("RefreshCharacterDossiers: pointer not found: {Pointer}", pointer);
                continue;
            }

            if (item.Type == LinearItemType.Heading)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(item.Markdown))
            {
                continue;
            }

            paragraphs.Add(new TextFragment(pointer, item.Markdown));
        }

        return paragraphs;
    }

    public async Task<CharacterDossiers> GenerateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var baseDossiers = dossierService.GetDossiers();
        var index = new DossierIndex(baseDossiers);

        var cursor = new FullScanCursorStream(
            documentContext.Document,
            limits.MaxElements,
            limits.MaxBytes,
            null,
            includeHeadings: false,
            logger);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var portion = cursor.NextPortion();
            if (portion.Items.Count == 0 && !portion.HasMore)
            {
                break;
            }

            var paragraphs = new List<(string Pointer, string Text)>(portion.Items.Count);
            foreach (var item in portion.Items)
            {
                if (item.Type == LinearItemType.Heading)
                {
                    continue;
                }

                var markdown = item.Markdown;
                if (string.IsNullOrWhiteSpace(markdown))
                {
                    continue;
                }

                paragraphs.Add((item.Pointer.ToCompactString(), markdown));
            }

            if (paragraphs.Count > 0)
            {
                await ApplyParagraphsAsync(index, paragraphs, cancellationToken);
            }

            if (!portion.HasMore)
            {
                break;
            }
        }

        var dossiers = index.ToDossiers(baseDossiers, limits.CharacterDossiersMaxCharacters);
        dossierService.ReplaceDossiers(dossiers.Characters);
        return dossierService.GetDossiers();
    }

    public async Task<CharacterDossiers> RefreshAsync(
        IReadOnlyCollection<string>? changedPointers,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (changedPointers == null || changedPointers.Count == 0)
        {
            return await GenerateAsync(cancellationToken);
        }

        var pointerSet = changedPointers
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToHashSet(StringComparer.Ordinal);

        if (pointerSet.Count == 0)
        {
            return dossierService.GetDossiers();
        }

        var lookup = documentContext.Document.Items
            .ToDictionary(i => i.Pointer.ToCompactString(), i => i, StringComparer.Ordinal);

        var paragraphs = new List<(string Pointer, string Text)>(pointerSet.Count);

        foreach (var pointer in pointerSet)
        {
            if (!lookup.TryGetValue(pointer, out var item))
            {
                logger.LogWarning("RefreshCharacterDossiers: pointer not found: {Pointer}", pointer);
                continue;
            }

            if (item.Type == LinearItemType.Heading)
            {
                continue;
            }

            var markdown = item.Markdown;
            if (string.IsNullOrWhiteSpace(markdown))
            {
                continue;
            }

            paragraphs.Add((pointer, markdown));
        }

        if (paragraphs.Count == 0)
        {
            return dossierService.GetDossiers();
        }

        var baseDossiers = dossierService.GetDossiers();
        var index = new DossierIndex(baseDossiers);
        var changed = await ApplyParagraphsAsync(index, paragraphs, cancellationToken);
        if (!changed)
        {
            return dossierService.GetDossiers();
        }

        var dossiers = index.ToDossiers(baseDossiers, limits.CharacterDossiersMaxCharacters);
        dossierService.ReplaceDossiers(dossiers.Characters);
        return dossierService.GetDossiers();
    }

    public async Task<CharacterDossiers> UpdateFromEvidenceBatchAsync(
        IReadOnlyList<EvidenceItem> evidence,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        cancellationToken.ThrowIfCancellationRequested();

        var paragraphs = CollectParagraphsFromEvidence(evidence);
        if (paragraphs.Count == 0)
        {
            return dossierService.GetDossiers();
        }

        var baseDossiers = dossierService.GetDossiers();
        var index = new DossierIndex(baseDossiers);

        var changed = false;
        foreach (var batch in SplitParagraphs(paragraphs))
        {
            changed |= await ApplyParagraphsAsync(index, batch, cancellationToken);
        }

        if (!changed)
        {
            return dossierService.GetDossiers();
        }

        var dossiers = index.ToDossiers(baseDossiers, limits.CharacterDossiersMaxCharacters);
        dossierService.ReplaceDossiers(dossiers.Characters);
        return dossierService.GetDossiers();
    }

    private static List<(string Pointer, string Text)> CollectParagraphsFromEvidence(IReadOnlyList<EvidenceItem> evidence)
    {
        return evidence
            .Where(e => !string.IsNullOrWhiteSpace(e.Pointer) && !string.IsNullOrWhiteSpace(e.Excerpt))
            .Select(e => (Pointer: e.Pointer.Trim(), Text: e.Excerpt!.Trim()))
            .Where(p => p.Pointer.Length > 0 && p.Text.Length > 0)
            .ToList();
    }

    private IEnumerable<List<(string Pointer, string Text)>> SplitParagraphs(
        IReadOnlyList<(string Pointer, string Text)> paragraphs)
    {
        if (paragraphs.Count == 0)
        {
            yield break;
        }

        var maxElements = Math.Max(1, limits.MaxElements);
        var maxBytes = Math.Max(1, limits.MaxBytes);

        var batch = new List<(string Pointer, string Text)>(Math.Min(maxElements, paragraphs.Count));
        var batchBytes = 0;

        foreach (var paragraph in paragraphs)
        {
            var text = paragraph.Text ?? string.Empty;
            var size = Encoding.UTF8.GetByteCount(text);
            var wouldOverflow = batch.Count >= maxElements || (batchBytes + size) > maxBytes;

            if (wouldOverflow && batch.Count > 0)
            {
                yield return batch;
                batch = new List<(string Pointer, string Text)>(Math.Min(maxElements, paragraphs.Count));
                batchBytes = 0;
            }

            batch.Add(paragraph);
            batchBytes += size;
        }

        if (batch.Count > 0)
        {
            yield return batch;
        }
    }

    private async Task<bool> ApplyParagraphsAsync(
        DossierIndex index,
        IReadOnlyList<(string Pointer, string Text)> paragraphs,
        CancellationToken cancellationToken)
    {
        if (paragraphs.Count == 0)
        {
            return false;
        }

        var hits = await ExtractCharactersWithModelAsync(paragraphs, cancellationToken);
        if (hits.Count == 0)
        {
            return false;
        }

        return await ApplyHitsAsync(index, hits, cancellationToken);
    }

    private async Task<bool> ApplyHitsAsync(
        DossierIndex index,
        IReadOnlyList<CharacterExtractionCharacter> hits,
        CancellationToken cancellationToken)
    {
        var changed = false;

        foreach (var hit in hits)
        {
            changed |= ApplyHitToIndex(index, hit, out _);
        }

        return changed;
    }

    private async Task<List<CharacterExtractionCharacter>> ExtractCharactersWithModelAsync(
        IReadOnlyList<(string Pointer, string Text)> paragraphs,
        CancellationToken cancellationToken)
    {
        if (paragraphs.Count == 0)
        {
            return [];
        }

        var payload = new
        {
            task = "extract_characters",
            paragraphs = paragraphs.Select(p => new { pointer = p.Pointer, text = p.Text })
        };

        var prompt = JsonSerializer.Serialize(payload, JsonOptions);

        var extractionResponse = await characterExtractionModelClient.ExtractCharactersAsync(
            new CharacterExtractionModelRequest(CharacterExtractionSystemPrompt,prompt),
            cancellationToken);

        return extractionResponse.Characters
            .Select(NormalizeHit)
            .Where(x => !string.IsNullOrWhiteSpace(x.CanonicalName))
            .ToList();
    }

    private static CharacterExtractionCharacter NormalizeHit(CharacterExtractionCharacter hit)
    {
        var canonical = (hit.CanonicalName ?? string.Empty).Trim();
        var description = (hit.Description ?? string.Empty).Trim();

        var normalizedAliases = (hit.Aliases ?? [])
            .Where(a => a is not null && !string.IsNullOrWhiteSpace(a.Form) && !string.IsNullOrWhiteSpace(a.Example))
            .Select(a => new CharacterExtractionAlias(a.Form.Trim(), a.Example.Trim()))
            .DistinctBy(a => a.Form, StringComparer.OrdinalIgnoreCase)
            .OrderBy(a => a.Form, StringComparer.OrdinalIgnoreCase)
            .ToList();

        normalizedAliases = AddPossessiveBaseAliases(normalizedAliases);

        return hit with
        {
            CanonicalName = canonical,
            Aliases = normalizedAliases,
            Gender = NormalizeGender(hit.Gender),
            Description = description
        };
    }

    private static CharacterBibleCharacterCandidate ToCandidate(CharacterExtractionCharacter hit)
    {
        var aliasExamples = (hit.Aliases ?? [])
            .Where(alias => !string.IsNullOrWhiteSpace(alias.Form) && !string.IsNullOrWhiteSpace(alias.Example))
            .ToDictionary(
                alias => alias.Form.Trim(),
                alias => alias.Example.Trim(),
                StringComparer.OrdinalIgnoreCase);

        return new CharacterBibleCharacterCandidate(
            hit.CanonicalName?.Trim() ?? string.Empty,
            NormalizeGender(hit.Gender),
            aliasExamples,
            hit.Description?.Trim() ?? string.Empty);
    }

    private static CharacterExtractionCharacter ToCharacterExtractionCharacter(CharacterBibleCharacterCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        var aliases = candidate.AliasExamples
            .Select(alias => new CharacterExtractionAlias(alias.Key, alias.Value))
            .ToList();

        return NormalizeHit(new CharacterExtractionCharacter(
            candidate.CanonicalName,
            candidate.Gender,
            aliases,
            candidate.Description));
    }

    private static List<CharacterExtractionAlias> AddPossessiveBaseAliases(List<CharacterExtractionAlias> aliases)
    {
        if (aliases.Count == 0)
        {
            return aliases;
        }

        var seen = new HashSet<string>(aliases.Select(a => a.Form), StringComparer.OrdinalIgnoreCase);
        var expanded = new List<CharacterExtractionAlias>(aliases);

        foreach (var alias in aliases)
        {
            if (!TryGetPossessiveBase(alias.Form, out var baseForm))
            {
                continue;
            }

            if (seen.Add(baseForm))
            {
                expanded.Add(new CharacterExtractionAlias(baseForm, alias.Example));
            }
        }

        return expanded
            .OrderBy(a => a.Form, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool TryGetPossessiveBase(string value, out string baseForm)
    {
        baseForm = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (trimmed.EndsWith("'s", StringComparison.OrdinalIgnoreCase) || trimmed.EndsWith("’s", StringComparison.OrdinalIgnoreCase))
        {
            baseForm = trimmed[..^2].Trim();
            return baseForm.Length > 0;
        }

        return false;
    }

    private static string NormalizeGender(string? gender)
    {
        if (string.IsNullOrWhiteSpace(gender))
        {
            return "unknown";
        }

        var g = gender.Trim().ToLowerInvariant();
        return g switch
        {
            "male" => "male",
            "female" => "female",
            _ => "unknown"
        };
    }

    private sealed record KeyVariant(string Key, int Weight);

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

            if (match != null)
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
                .Select(x => x.ToDossier())
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (maxCharacters.HasValue && maxCharacters.Value > 0 && items.Count > maxCharacters.Value)
            {
                items = items.Take(maxCharacters.Value).ToList();
            }

            return baseDossiers with { Characters = items };
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
                    .Where(p => p.MatchesName(canonical))
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
    }

    private sealed class ProfileAccumulator
    {
        private readonly Dictionary<string, string> aliasExamples = new(StringComparer.OrdinalIgnoreCase);

        public string Id { get; }
        public string Name { get; private set; }
        public string Description { get; private set; }
        public string Gender { get; private set; }
        public IReadOnlyDictionary<string, string> AliasExamples => aliasExamples;

        public ProfileAccumulator(CharacterDossier dossier)
        {
            Id = dossier.CharacterId;
            Name = dossier.Name;
            Description = dossier.Description;
            Gender = string.IsNullOrWhiteSpace(dossier.Gender) ? "unknown" : dossier.Gender;

            MergeAliasExamples(dossier.AliasExamples);
        }

        private ProfileAccumulator(string name, string gender, IEnumerable<CharacterExtractionAlias>? aliases, string? description)
        {
            Name = name.Trim();
            using var md5 = System.Security.Cryptography.MD5.Create();
            var hash = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(Name));
            Id = new Guid(hash).ToString("N");

            Gender = string.IsNullOrWhiteSpace(gender) ? "unknown" : gender.Trim();
            Description = description?.Trim() ?? string.Empty;

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
            return new ProfileAccumulator(name, candidate.Gender ?? "unknown", candidate.Aliases, candidate.Description);
        }

        public bool MatchesName(string name)
            => string.Equals(Name, name?.Trim(), StringComparison.OrdinalIgnoreCase);

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

        public bool SetDescriptionIfEmpty(string? description)
        {
            var normalized = description?.Trim() ?? string.Empty;
            if (normalized.Length == 0)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(Description))
            {
                return false;
            }

            Description = normalized;
            return true;
        }

        public CharacterDossier ToDossier()
        {
            var normalizedAliasExamples = aliasExamples
                .Where(kvp => !string.IsNullOrWhiteSpace(kvp.Key) && !string.IsNullOrWhiteSpace(kvp.Value))
                .ToDictionary(kvp => kvp.Key.Trim(), kvp => kvp.Value.Trim(), StringComparer.OrdinalIgnoreCase);

            var aliases = normalizedAliasExamples.Keys
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(a => a, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new CharacterDossier(
                Id,
                Name,
                Description?.Trim() ?? string.Empty,
                aliases,
                normalizedAliasExamples,
                [],
                string.IsNullOrWhiteSpace(Gender) ? "unknown" : Gender);
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

            var match = aliases.FirstOrDefault(a => string.Equals(a.Form, name, StringComparison.OrdinalIgnoreCase));
            if (match is null || string.IsNullOrWhiteSpace(match.Example))
            {
                return false;
            }

            example = match.Example;
            return true;
        }
    }

    private static List<KeyVariant> BuildCandidateKeyVariants(CharacterExtractionCharacter candidate)
    {
        var variants = new List<KeyVariant>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddVariants(candidate.CanonicalName, 4, 3);

        if (candidate.Aliases is { Count: > 0 })
        {
            foreach (var alias in candidate.Aliases)
            {
                AddVariants(alias.Form, 3, 2);

                if (TryGetPossessiveBase(alias.Form, out var baseForm))
                {
                    AddVariants(baseForm, 2, 1);
                }
            }
        }

        return variants;

        void AddVariants(string? value, int baseWeight, int strippedWeight)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var baseKey = NormalizeKey(value);
            if (!string.IsNullOrWhiteSpace(baseKey) && seen.Add(baseKey))
            {
                variants.Add(new KeyVariant(baseKey, baseWeight));
            }
        }
    }

    private static int ResolveMinMatchScore(CharacterExtractionCharacter candidate)
    {
        var canonicalKey = NormalizeKey(candidate.CanonicalName ?? string.Empty);
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

            var baseKey = NormalizeKey(value);
            if (!string.IsNullOrWhiteSpace(baseKey))
            {
                keys.Add(baseKey);
            }

            if (TryGetPossessiveBase(value, out var possessiveBase))
            {
                var basePossessiveKey = NormalizeKey(possessiveBase);
                if (!string.IsNullOrWhiteSpace(basePossessiveKey))
                {
                    keys.Add(basePossessiveKey);
                }
            }
        }
    }

    private static string NormalizeKey(string value)
    {
        var normalized = NormalizeForComparison(value);
        return normalized;
    }

    private static string NormalizeForComparison(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var lowered = value.Trim().ToLowerInvariant().Replace('ё', 'е');
        var builder = new StringBuilder(lowered.Length);
        var lastWasSpace = false;

        foreach (var ch in lowered)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
                lastWasSpace = false;
                continue;
            }

            if (char.IsWhiteSpace(ch) || ch == '-' || ch == '—' || ch == '–' || ch == '_')
            {
                if (!lastWasSpace)
                {
                    builder.Append(' ');
                    lastWasSpace = true;
                }
            }
        }

        return builder.ToString().Trim();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

private const string CharacterExtractionSystemPrompt = """
You are an expert Character Extractor.

Task: Analyze the provided text fragments and extract structured data about CHARACTERS/PEOPLE mentioned.

Input: JSON { "task": "extract_characters", "paragraphs": [ { "pointer": "...", "text": "..." } ] }

General Rules:
- Identify only PEOPLE/CHARACTERS. Ignore generic groups and unnamed speakers.
- If a person is referenced only by a role/title (e.g., "the professor"), include it ONLY if it clearly refers to the same single person within the provided input; otherwise ignore.
- Use ONLY the provided text. Do not invent facts.
- Output generic/cliché entries is forbidden.
- If no characters found, return [].

Structured response contract:
- characters: array of character objects.
- character.canonicalName: best stable name as in text, without title; do NOT invent patronymics/missing parts.
- character.gender: male, female, or unknown.
- character.aliases: array of name variants found in text. Each alias object MUST contain exactly "form" and "example"; "example" is a short sentence containing this form.
- character.description: 2-5 sentences, see below.

Name Rules:
- Canonical Name: Prefer the best stable name as in text WITHOUT title. Do NOT invent patronymics or missing parts of the name.
- If title is needed for disambiguation, include it in canonicalName, but add an alias without title.
- Aliases: Must belong to the SAME character. Do not merge different characters listed together.
- One object per character. Merge mentions across paragraphs. If unsure two mentions are the same person, keep separate objects.
- Aliases: Keep up to 5. Prefer the most informative forms.

Description Rules (Critical):
- Language: RUSSIAN.
- Create a cohesive psychological portrait (temperament, habits, values) ONLY based on EXPLICIT evidence in the text (actions, dialogue, internal monologue).
- If the character is merely present (e.g., "was there", "flew with X") but their personality is not revealed, write exactly: "В данном фрагменте характер не раскрыт."
- STRICTLY FORBIDDEN: Inventing positive/professional traits (e.g., "initiative", "attentive", "supportive") without direct evidence.
- STRICTLY FORBIDDEN: Generalizing single ambiguous actions into permanent personality traits.
- You may infer weak traits ONLY with uncertainty words ("вероятно", "похоже") and only if supported by at least TWO separate cues.
- Abstract generalizations; DO NOT retell scenes or quote text.
- Plain prose only (no lists).
- Usage of meta-phrases ("in the book", "author says", "в тексте") is FORBIDDEN (except for the "характер не раскрыт" phrase).
- Do NOT use double quotes (") inside any string fields.

Output:
Populate only the structured response contract. If no characters are found, return an empty characters collection.
""";
}
