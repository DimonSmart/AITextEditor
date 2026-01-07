using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiTextEditor.Lib.Model;
using AiTextEditor.Lib.Services;
using AiTextEditor.Lib.Services.SemanticKernel;
using DimonSmart.AiUtils;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace AiTextEditor.SemanticKernel;

public sealed class CharacterDossiersGenerator
{
    private readonly IDocumentContext documentContext;
    private readonly CharacterDossierService dossierService;
    private readonly CursorAgentLimits limits;
    private readonly ILogger<CharacterDossiersGenerator> logger;
    private readonly IChatCompletionService chatService;

    public CharacterDossiersGenerator(
        IDocumentContext documentContext,
        CharacterDossierService dossierService,
        CursorAgentLimits limits,
        ILogger<CharacterDossiersGenerator> logger,
        IChatCompletionService chatService)
    {
        this.documentContext = documentContext ?? throw new ArgumentNullException(nameof(documentContext));
        this.dossierService = dossierService ?? throw new ArgumentNullException(nameof(dossierService));
        this.limits = limits ?? throw new ArgumentNullException(nameof(limits));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.chatService = chatService ?? throw new ArgumentNullException(nameof(chatService));
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

        var hits = await ExtractCharactersWithLlmAsync(paragraphs, cancellationToken);
        if (hits.Count == 0)
        {
            return false;
        }

        return await ApplyHitsAsync(index, hits, cancellationToken);
    }

    private async Task<bool> ApplyHitsAsync(
        DossierIndex index,
        IReadOnlyList<LlmCharacter> hits,
        CancellationToken cancellationToken)
    {
        var changed = false;
        var profilesWithNewFacts = new HashSet<ProfileAccumulator>();

        foreach (var hit in hits)
        {
            var (profile, created, exactNameMatch) = index.MatchOrCreate(hit);
            changed |= created;

            var anyAliasChanged = profile.MergeAliases(hit, exactNameMatch);
            changed |= anyAliasChanged;

            changed |= profile.SetGenderIfUnknown(hit.Gender);

            var factsChanged = profile.MergeFacts(hit.Facts);
            if (factsChanged)
            {
                profilesWithNewFacts.Add(profile);
                changed = true;
            }

            if (created || anyAliasChanged || factsChanged)
            {
                index.UpdateKeys(profile);
            }
        }

        foreach (var profile in profilesWithNewFacts)
        {
            var description = await SummarizeDescriptionAsync(profile, cancellationToken);
            if (profile.SetDescriptionIfChanged(description))
            {
                changed = true;
            }
        }

        return changed;
    }

    private async Task<string> SummarizeDescriptionAsync(ProfileAccumulator profile, CancellationToken cancellationToken)
    {
        if (profile.Facts.Count == 0)
        {
            return string.Empty;
        }

        var payload = new
        {
            task = "summarize_description_from_facts",
            name = profile.Name,
            gender = profile.Gender,
            facts = profile.Facts.Select(f => new { key = f.Key, value = f.Value }).ToArray()
        };

        var prompt = JsonSerializer.Serialize(payload, JsonOptions);

        var history = new ChatHistory();
        history.AddSystemMessage(CharacterDescriptionSummarizerSystemPrompt);
        history.AddUserMessage(prompt);

        var settings = new OpenAIPromptExecutionSettings
        {
            Temperature = 0,
            MaxTokens = 300
        };

        var response = await chatService.GetChatMessageContentsAsync(history, settings, cancellationToken: cancellationToken);
        var content = (response.FirstOrDefault()?.Content ?? string.Empty).Trim();
        return content;
    }

    private async Task<List<LlmCharacter>> ExtractCharactersWithLlmAsync(
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

        var history = new ChatHistory();
        history.AddSystemMessage(CharacterExtractionSystemPrompt);
        history.AddUserMessage(prompt);

        var settings = new OpenAIPromptExecutionSettings
        {
            Temperature = 0,
            MaxTokens = 1600
        };

        var response = await chatService.GetChatMessageContentsAsync(history, settings, cancellationToken: cancellationToken);
        var content = response.FirstOrDefault()?.Content ?? string.Empty;

        var json = JsonExtractor.ExtractJson(content);
        if (string.IsNullOrWhiteSpace(json))
        {
            logger.LogWarning("Character extraction returned no JSON.");
            return [];
        }

        try
        {
            var items = JsonSerializer.Deserialize<List<LlmCharacter>>(json, JsonOptions) ?? [];
            return items
                .Select(NormalizeHit)
                .Where(x => !string.IsNullOrWhiteSpace(x.CanonicalName))
                .ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Character extraction JSON parse failed.");
            return [];
        }
    }

    private static LlmCharacter NormalizeHit(LlmCharacter hit)
    {
        var canonical = (hit.CanonicalName ?? string.Empty).Trim();

        var normalizedAliases = (hit.Aliases ?? [])
            .Where(a => a is not null && !string.IsNullOrWhiteSpace(a.Form) && !string.IsNullOrWhiteSpace(a.Example))
            .Select(a => new AliasHit(a.Form.Trim(), a.Example.Trim()))
            .DistinctBy(a => a.Form, StringComparer.OrdinalIgnoreCase)
            .OrderBy(a => a.Form, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var normalizedFacts = (hit.Facts ?? [])
            .Where(f => f is not null && !string.IsNullOrWhiteSpace(f.Key) && !string.IsNullOrWhiteSpace(f.Value) && !string.IsNullOrWhiteSpace(f.Example))
            .Select(f => new FactHit(f.Key.Trim(), f.Value.Trim(), f.Example.Trim()))
            .DistinctBy(f => (NormalizeForComparison(f.Key), NormalizeForComparison(f.Value)))
            .OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Value, StringComparer.OrdinalIgnoreCase)
            .ToList();

        normalizedAliases = AddPossessiveBaseAliases(normalizedAliases);

        return hit with
        {
            CanonicalName = canonical,
            Aliases = normalizedAliases,
            Facts = normalizedFacts,
            Gender = NormalizeGender(hit.Gender)
        };
    }

    private static List<AliasHit> AddPossessiveBaseAliases(List<AliasHit> aliases)
    {
        if (aliases.Count == 0)
        {
            return aliases;
        }

        var seen = new HashSet<string>(aliases.Select(a => a.Form), StringComparer.OrdinalIgnoreCase);
        var expanded = new List<AliasHit>(aliases);

        foreach (var alias in aliases)
        {
            if (!TryGetPossessiveBase(alias.Form, out var baseForm))
            {
                continue;
            }

            if (seen.Add(baseForm))
            {
                expanded.Add(new AliasHit(baseForm, alias.Example));
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

    private sealed record AliasHit(
        [property: JsonPropertyName("form")] string Form,
        [property: JsonPropertyName("example")] string Example);

    private sealed record FactHit(
        [property: JsonPropertyName("key")] string Key,
        [property: JsonPropertyName("value")] string Value,
        [property: JsonPropertyName("example")] string Example);

    private sealed record LlmCharacter(
        [property: JsonPropertyName("canonicalName")] string? CanonicalName,
        [property: JsonPropertyName("gender")] string? Gender,
        [property: JsonPropertyName("aliases")] List<AliasHit>? Aliases,
        [property: JsonPropertyName("facts")] List<FactHit>? Facts);

    private sealed record KeyVariant(string Key, int Weight);

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

        public (ProfileAccumulator Profile, bool Created, bool ExactNameMatch) MatchOrCreate(LlmCharacter candidate)
        {
            var (match, exactNameMatch) = FindMatch(candidate);
            if (match != null)
            {
                return (match, false, exactNameMatch);
            }

            var accumulator = ProfileAccumulator.FromCandidate(candidate);
            profiles[accumulator.Id] = accumulator;
            IndexProfile(accumulator);
            return (accumulator, true, true);
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

        private (ProfileAccumulator? Match, bool ExactNameMatch) FindMatch(LlmCharacter candidate)
        {
            var canonical = candidate.CanonicalName?.Trim();
            if (!string.IsNullOrWhiteSpace(canonical))
            {
                var exact = profiles.Values
                    .Where(p => p.MatchesName(canonical))
                    .ToList();

                if (exact.Count == 1)
                {
                    return (exact[0], true);
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
                return (null, false);
            }

            var bestScore = scores.Values.Max();
            var threshold = ResolveMinMatchScore(candidate);
            if (bestScore < threshold)
            {
                return (null, false);
            }

            var best = scores
                .Where(kvp => kvp.Value == bestScore)
                .Select(kvp => kvp.Key)
                .ToList();

            return best.Count == 1 ? (best[0], false) : (null, false);
        }
    }

    private sealed class ProfileAccumulator
    {
        private readonly Dictionary<string, string> aliasExamples = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<CharacterFact> facts = [];

        public string Id { get; }
        public string Name { get; private set; }
        public string Description { get; private set; }
        public string Gender { get; private set; }
        public IReadOnlyDictionary<string, string> AliasExamples => aliasExamples;
        public IReadOnlyList<CharacterFact> Facts => facts;

        public ProfileAccumulator(CharacterDossier dossier)
        {
            Id = dossier.CharacterId;
            Name = dossier.Name;
            Description = dossier.Description;
            Gender = string.IsNullOrWhiteSpace(dossier.Gender) ? "unknown" : dossier.Gender;

            MergeAliasExamples(dossier.AliasExamples);
            facts.AddRange(dossier.Facts ?? []);
        }

        private ProfileAccumulator(string name, string gender, IEnumerable<AliasHit>? aliases, IEnumerable<FactHit>? facts)
        {
            Id = Guid.NewGuid().ToString("N");
            Name = name.Trim();
            Gender = string.IsNullOrWhiteSpace(gender) ? "unknown" : gender.Trim();
            Description = string.Empty;

            if (aliases is not null)
            {
                foreach (var alias in aliases)
                {
                    AddAliasExample(alias.Form, alias.Example);
                }
            }

            if (facts is not null)
            {
                foreach (var fact in facts)
                {
                    AddFact(fact.Key, fact.Value, fact.Example);
                }
            }
        }

        public static ProfileAccumulator FromCandidate(LlmCharacter candidate)
        {
            var name = candidate.CanonicalName?.Trim() ?? string.Empty;
            return new ProfileAccumulator(name, candidate.Gender ?? "unknown", candidate.Aliases, candidate.Facts);
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

        public bool MergeAliases(LlmCharacter candidate, bool exactNameMatch)
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

        public bool MergeFacts(IEnumerable<FactHit>? incoming)
        {
            if (incoming is null)
            {
                return false;
            }

            var changed = false;
            foreach (var fact in incoming)
            {
                changed |= AddFact(fact.Key, fact.Value, fact.Example);
            }

            return changed;
        }

        public bool SetDescriptionIfChanged(string description)
        {
            var normalized = description?.Trim() ?? string.Empty;
            if (string.Equals(Description?.Trim() ?? string.Empty, normalized, StringComparison.Ordinal))
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

            var normalizedFacts = facts
                .Where(f => f is not null)
                .GroupBy(f => (NormalizeForComparison(f.Key), NormalizeForComparison(f.Value)))
                .Select(g => g.First())
                .OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase)
                .ThenBy(f => f.Value, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new CharacterDossier(
                Id,
                Name,
                Description?.Trim() ?? string.Empty,
                aliases,
                normalizedAliasExamples,
                normalizedFacts,
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

        private bool AddFact(string key, string value, string example)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(example))
            {
                return false;
            }

            var normalizedKey = key.Trim();
            var normalizedValue = value.Trim();
            var normalizedExample = example.Trim();

            var exists = facts.Any(f =>
                string.Equals(f.Key, normalizedKey, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(f.Value, normalizedValue, StringComparison.OrdinalIgnoreCase));

            if (exists)
            {
                return false;
            }

            facts.Add(new CharacterFact(normalizedKey, normalizedValue, normalizedExample));
            return true;
        }

        private static bool TryFindExampleFor(string name, IReadOnlyList<AliasHit>? aliases, out string example)
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

    private static List<KeyVariant> BuildCandidateKeyVariants(LlmCharacter candidate)
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

            var baseKey = NormalizeKey(value, stripTitles: false);
            if (!string.IsNullOrWhiteSpace(baseKey) && seen.Add(baseKey))
            {
                variants.Add(new KeyVariant(baseKey, baseWeight));
            }

            var strippedKey = NormalizeKey(value, stripTitles: true);
            if (!string.IsNullOrWhiteSpace(strippedKey)
                && !string.Equals(strippedKey, baseKey, StringComparison.Ordinal)
                && seen.Add(strippedKey))
            {
                variants.Add(new KeyVariant(strippedKey, strippedWeight));
            }
        }
    }

    private static int ResolveMinMatchScore(LlmCharacter candidate)
    {
        var canonicalKey = NormalizeKey(candidate.CanonicalName ?? string.Empty, stripTitles: true);
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

            var baseKey = NormalizeKey(value, stripTitles: false);
            if (!string.IsNullOrWhiteSpace(baseKey))
            {
                keys.Add(baseKey);
            }

            var strippedKey = NormalizeKey(value, stripTitles: true);
            if (!string.IsNullOrWhiteSpace(strippedKey))
            {
                keys.Add(strippedKey);
            }

            if (TryGetPossessiveBase(value, out var possessiveBase))
            {
                var basePossessiveKey = NormalizeKey(possessiveBase, stripTitles: false);
                if (!string.IsNullOrWhiteSpace(basePossessiveKey))
                {
                    keys.Add(basePossessiveKey);
                }

                var strippedPossessiveKey = NormalizeKey(possessiveBase, stripTitles: true);
                if (!string.IsNullOrWhiteSpace(strippedPossessiveKey))
                {
                    keys.Add(strippedPossessiveKey);
                }
            }
        }
    }

    private static string NormalizeKey(string value, bool stripTitles)
    {
        var normalized = NormalizeForComparison(value);
        if (!stripTitles || string.IsNullOrWhiteSpace(normalized))
        {
            return normalized;
        }

        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            return normalized;
        }

        var index = 0;
        while (index < tokens.Length && TitleTokens.Contains(tokens[index]))
        {
            index++;
        }

        return index == 0 ? normalized : string.Join(' ', tokens.Skip(index));
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

    private static readonly HashSet<string> TitleTokens = new(StringComparer.Ordinal)
    {
        "профессор",
        "проф",
        "доктор",
        "д-р",
        "господин",
        "госпожа",
        "товарищ",
        "мистер",
        "мисс",
        "миссис",
        "сэр",
        "мсье",
        "мадам",
        "мадемуазель",
        "professor",
        "prof",
        "doctor",
        "dr",
        "mr",
        "mrs",
        "ms",
        "sir"
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    private const string CharacterDescriptionSummarizerSystemPrompt = """
You are CharacterDossierDescriptionSummarizer.

Input: JSON:
{
  "task": "summarize_description_from_facts",
  "name": "...",
  "gender": "male|female|unknown",
  "facts": [ { "key": "...", "value": "..." } ]
}

Rules:
- Use ONLY the provided facts. Do not invent.
- Output must be one line: 1-3 sentences in the same language as the facts.
- If facts is empty, output an empty string.
- Do not mention pointers, dates, languages, or metadata.

Output:
Return PLAIN TEXT ONLY. No JSON. No markdown.
""";

    private const string CharacterExtractionSystemPrompt = """
You are CharacterExtractor.

Input: JSON:
{ "task": "extract_characters", "paragraphs": [ { "pointer": "...", "text": "..." } ] }

Rules:
- Identify only PEOPLE/CHARACTERS mentioned in the text.
- If a paragraph contains no character mentions, ignore it.
- Do not guess. If it is unclear that an entity is a person, skip it.
- Ignore generic groups/roles or unnamed speakers.
- Treat a role as a character only when it is part of a stable unique designation tied to a person.
- Use ONLY the provided text. Do not invent facts.
- Extract structured data only: canonical name, name forms (with a short sentence example), and explicit facts (with a short sentence example).

Candidate schema (JSON array only):
[
  {
    "canonicalName": "string (required)",
    "gender": "male|female|unknown",
    "aliases": [
      { "form": "string", "example": "short sentence from the text" }
    ],
    "facts": [
      { "key": "string", "value": "string", "example": "short sentence from the text" }
    ]
  }
]

Important:
- aliases must contain ONLY forms/nicknames of THIS SAME character. Never include other characters.
- If a sentence lists multiple names separated by commas or 'and'/'и', those are DIFFERENT characters, not aliases.
- facts must be stable properties/status/relations explicitly stated in the text, not one-off actions.
- Forbidden to 'fill in' or infer.

English possessive rule:
- If you see a form like John's or John’s, include alias with that form and ALSO include base form John (you may reuse the same example).

Output:
Return JSON ARRAY ONLY. No markdown. No extra text.
""";
}
