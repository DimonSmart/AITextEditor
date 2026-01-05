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

public sealed class CharacterRosterGenerator
{
    private readonly IDocumentContext documentContext;
    private readonly CharacterRosterService rosterService;
    private readonly CursorAgentLimits limits;
    private readonly ILogger<CharacterRosterGenerator> logger;
    private readonly IChatCompletionService chatService;

    public CharacterRosterGenerator(
        IDocumentContext documentContext,
        CharacterRosterService rosterService,
        CursorAgentLimits limits,
        ILogger<CharacterRosterGenerator> logger,
        IChatCompletionService chatService)
    {
        this.documentContext = documentContext ?? throw new ArgumentNullException(nameof(documentContext));
        this.rosterService = rosterService ?? throw new ArgumentNullException(nameof(rosterService));
        this.limits = limits ?? throw new ArgumentNullException(nameof(limits));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.chatService = chatService ?? throw new ArgumentNullException(nameof(chatService));
    }

    public async Task<CharacterRoster> GenerateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var baseRoster = rosterService.GetRoster();
        var index = new RosterIndex(baseRoster);

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

        var roster = index.ToRoster(baseRoster, limits.CharacterRosterMaxCharacters);
        rosterService.ReplaceRoster(roster.Characters);
        return rosterService.GetRoster();
    }

    public async Task<CharacterRoster> RefreshAsync(
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
            return rosterService.GetRoster();
        }

        var lookup = documentContext.Document.Items
            .ToDictionary(i => i.Pointer.ToCompactString(), i => i, StringComparer.Ordinal);

        var paragraphs = new List<(string Pointer, string Text)>(pointerSet.Count);

        foreach (var pointer in pointerSet)
        {
            if (!lookup.TryGetValue(pointer, out var item))
            {
                logger.LogWarning("RefreshCharacterRoster: pointer not found: {Pointer}", pointer);
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
            return rosterService.GetRoster();
        }

        var baseRoster = rosterService.GetRoster();
        var index = new RosterIndex(baseRoster);
        var changed = await ApplyParagraphsAsync(index, paragraphs, cancellationToken);
        if (!changed)
        {
            return rosterService.GetRoster();
        }

        var roster = index.ToRoster(baseRoster, limits.CharacterRosterMaxCharacters);
        rosterService.ReplaceRoster(roster.Characters);
        return rosterService.GetRoster();
    }

    public async Task<CharacterRoster> GenerateFromEvidenceAsync(
        IReadOnlyList<EvidenceItem> evidence,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        cancellationToken.ThrowIfCancellationRequested();

        var paragraphs = CollectParagraphsFromEvidence(evidence);

        if (paragraphs.Count == 0)
        {
            return rosterService.GetRoster();
        }

        var baseRoster = rosterService.GetRoster();
        var index = new RosterIndex(baseRoster);
        var changed = false;
        foreach (var batch in SplitParagraphs(paragraphs))
        {
            changed |= await ApplyParagraphsAsync(index, batch, cancellationToken);
        }

        if (!changed)
        {
            return rosterService.GetRoster();
        }

        var roster = index.ToRoster(baseRoster, limits.CharacterRosterMaxCharacters);
        rosterService.ReplaceRoster(roster.Characters);
        return rosterService.GetRoster();
    }

    public async Task<CharacterRoster> UpdateFromEvidenceBatchAsync(
        IReadOnlyList<EvidenceItem> evidence,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        cancellationToken.ThrowIfCancellationRequested();

        var paragraphs = CollectParagraphsFromEvidence(evidence);
        if (paragraphs.Count == 0)
        {
            return rosterService.GetRoster();
        }

        var baseRoster = rosterService.GetRoster();
        var index = new RosterIndex(baseRoster);
        var changed = false;
        foreach (var batch in SplitParagraphs(paragraphs))
        {
            changed |= await ApplyParagraphsAsync(index, batch, cancellationToken);
        }

        if (!changed)
        {
            return rosterService.GetRoster();
        }

        var roster = index.ToRoster(baseRoster, limits.CharacterRosterMaxCharacters);
        rosterService.ReplaceRoster(roster.Characters);
        return rosterService.GetRoster();
    }

    private static List<(string Pointer, string Text)> CollectParagraphsFromEvidence(IReadOnlyList<EvidenceItem> evidence)
    {
        return evidence
            .Where(e => !string.IsNullOrWhiteSpace(e.Pointer) && !string.IsNullOrWhiteSpace(e.Excerpt))
            .Select(e => (Pointer: e.Pointer!.Trim(), Text: e.Excerpt!.Trim()))
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
        RosterIndex index,
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
        RosterIndex index,
        IReadOnlyList<LlmCharacter> hits,
        CancellationToken cancellationToken)
    {
        var changed = false;
        foreach (var hit in hits)
        {
            changed |= await ApplyCandidateAsync(index, hit, cancellationToken);
        }

        return changed;
    }

    private async Task<bool> ApplyCandidateAsync(
        RosterIndex index,
        LlmCharacter candidate,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(candidate.CanonicalName))
        {
            return false;
        }

        var profile = index.MatchOrCreate(candidate, out var created);
        var changed = created;

        if (!profile.MatchesName(candidate.CanonicalName))
        {
            changed |= profile.AddAlias(candidate.CanonicalName!);
        }

        if (candidate.Aliases is { Count: > 0 })
        {
            changed |= profile.AddAliases(candidate.Aliases);
        }

        changed |= profile.SetGenderIfUnknown(candidate.Gender);
        changed |= await TryUpdateDescriptionAsync(profile, candidate, cancellationToken);

        if (changed)
        {
            index.UpdateKeys(profile);
        }

        return changed;
    }

    private async Task<bool> TryUpdateDescriptionAsync(
        ProfileAccumulator profile,
        LlmCharacter candidate,
        CancellationToken cancellationToken)
    {
        var candidateDescription = candidate.Description?.Trim();
        if (IsTrivialDescription(candidateDescription, candidate.CanonicalName))
        {
            return false;
        }

        if (IsTrivialDescription(profile.Description, profile.Name))
        {
            if (string.IsNullOrWhiteSpace(candidateDescription) || DescriptionsEquivalent(profile.Description, candidateDescription))
            {
                return false;
            }

            profile.SetDescription(candidateDescription);
            return true;
        }

        if (DescriptionsEquivalent(profile.Description, candidateDescription))
        {
            return false;
        }

        var refined = await RefineDescriptionAsync(profile, candidate, cancellationToken);
        if (string.IsNullOrWhiteSpace(refined) || DescriptionsEquivalent(profile.Description, refined))
        {
            return false;
        }

        profile.SetDescription(refined);
        return true;
    }

    private async Task<string?> RefineDescriptionAsync(
        ProfileAccumulator profile,
        LlmCharacter candidate,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            task = "refine_description",
            current = new
            {
                name = profile.Name,
                description = profile.Description,
                gender = profile.Gender,
                aliases = profile.Aliases.ToArray()
            },
            candidate = new
            {
                canonicalName = candidate.CanonicalName,
                description = candidate.Description,
                gender = candidate.Gender,
                aliases = candidate.Aliases ?? []
            }
        };

        var prompt = JsonSerializer.Serialize(payload, JsonOptions);

        var history = new ChatHistory();
        history.AddSystemMessage(CharacterDescriptionRefinerSystemPrompt);
        history.AddUserMessage(prompt);

        var settings = new OpenAIPromptExecutionSettings
        {
            Temperature = 0,
            MaxTokens = 800
        };

        var response = await chatService.GetChatMessageContentsAsync(history, settings, cancellationToken: cancellationToken);
        var content = response.FirstOrDefault()?.Content ?? string.Empty;

        var parsed = ParseRefineResponse(content);
        if (parsed is null || !parsed.Update || string.IsNullOrWhiteSpace(parsed.Description))
        {
            return null;
        }

        return parsed.Description.Trim();
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

        var aliases = (hit.Aliases ?? [])
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a.Trim())
            .Where(a => !string.Equals(a, canonical, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(a => a, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var description = string.IsNullOrWhiteSpace(hit.Description) ? null : hit.Description.Trim();

        return hit with
        {
            CanonicalName = canonical,
            Aliases = aliases,
            Description = description,
            Gender = NormalizeGender(hit.Gender)
        };
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

    private sealed record LlmCharacter(
        [property: JsonPropertyName("canonicalName")] string? CanonicalName,
        [property: JsonPropertyName("aliases")] List<string>? Aliases,
        [property: JsonPropertyName("gender")] string? Gender,
        [property: JsonPropertyName("description")] string? Description);

    private sealed record DescriptionRefineResult(bool Update, string? Description);

    private sealed record KeyVariant(string Key, int Weight);

    private sealed class RosterIndex
    {
        private readonly Dictionary<string, ProfileAccumulator> profiles = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, HashSet<ProfileAccumulator>> keyIndex = new(StringComparer.OrdinalIgnoreCase);

        public RosterIndex(CharacterRoster roster)
        {
            foreach (var profile in roster.Characters)
            {
                var accumulator = new ProfileAccumulator(profile);
                profiles[accumulator.Id] = accumulator;
                IndexProfile(accumulator);
            }
        }

        public ProfileAccumulator MatchOrCreate(LlmCharacter candidate, out bool created)
        {
            var match = FindMatch(candidate);
            if (match != null)
            {
                created = false;
                return match;
            }

            created = true;
            var accumulator = ProfileAccumulator.FromCandidate(candidate);
            profiles[accumulator.Id] = accumulator;
            IndexProfile(accumulator);
            return accumulator;
        }

        public void UpdateKeys(ProfileAccumulator profile)
        {
            IndexProfile(profile);
        }

        public CharacterRoster ToRoster(CharacterRoster baseRoster, int? maxCharacters)
        {
            var roster = profiles.Values
                .Select(x => x.ToProfile())
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (maxCharacters.HasValue && maxCharacters.Value > 0 && roster.Count > maxCharacters.Value)
            {
                roster = roster.Take(maxCharacters.Value).ToList();
            }

            return baseRoster with { Characters = roster };
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

        private ProfileAccumulator? FindMatch(LlmCharacter candidate)
        {
            var canonical = candidate.CanonicalName?.Trim();
            if (!string.IsNullOrWhiteSpace(canonical))
            {
                var exact = profiles.Values
                    .Where(p => p.MatchesName(canonical))
                    .ToList();

                if (exact.Count == 1)
                {
                    return exact[0];
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
                return null;
            }

            var bestScore = scores.Values.Max();
            if (bestScore < MinMatchScore)
            {
                return null;
            }

            var best = scores
                .Where(kvp => kvp.Value == bestScore)
                .Select(kvp => kvp.Key)
                .ToList();

            return best.Count == 1 ? best[0] : null;
        }
    }

    private sealed class ProfileAccumulator
    {
        private readonly HashSet<string> aliases = new(StringComparer.OrdinalIgnoreCase);

        public string Id { get; }
        public string Name { get; private set; }
        public string Description { get; private set; }
        public string Gender { get; private set; }
        public IReadOnlyCollection<string> Aliases => aliases;

        public ProfileAccumulator(CharacterProfile profile)
        {
            Id = profile.CharacterId;
            Name = profile.Name;
            Description = profile.Description;
            Gender = string.IsNullOrWhiteSpace(profile.Gender) ? "unknown" : profile.Gender;

            AddAliases(profile.Aliases);
        }

        private ProfileAccumulator(string name, string description, string gender, IEnumerable<string>? aliases = null)
        {
            Id = Guid.NewGuid().ToString("N");
            Name = name.Trim();
            Description = description.Trim();
            Gender = string.IsNullOrWhiteSpace(gender) ? "unknown" : gender.Trim();

            if (aliases is not null)
            {
                AddAliases(aliases);
            }
        }

        public static ProfileAccumulator FromCandidate(LlmCharacter candidate)
        {
            var name = candidate.CanonicalName?.Trim() ?? string.Empty;
            var description = IsTrivialDescription(candidate.Description, name) ? name : candidate.Description!.Trim();
            return new ProfileAccumulator(name, description, candidate.Gender ?? "unknown", candidate.Aliases);
        }

        public bool MatchesName(string name)
            => string.Equals(Name, name?.Trim(), StringComparison.OrdinalIgnoreCase);

        public bool AddAlias(string alias)
        {
            if (string.IsNullOrWhiteSpace(alias))
            {
                return false;
            }

            var trimmed = alias.Trim();
            if (string.Equals(trimmed, Name, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return aliases.Add(trimmed);
        }

        public bool AddAliases(IEnumerable<string> aliases)
        {
            var changed = false;
            foreach (var alias in aliases)
            {
                if (AddAlias(alias))
                {
                    changed = true;
                }
            }

            return changed;
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

        public void SetDescription(string description)
        {
            Description = description.Trim();
        }

        public CharacterProfile ToProfile()
        {
            var aliasList = aliases
                .Where(a => !string.Equals(a, Name, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(a => a, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var desc = string.IsNullOrWhiteSpace(Description) ? Name : Description;

            return new CharacterProfile(
                Id,
                Name,
                desc,
                aliasList,
                string.IsNullOrWhiteSpace(Gender) ? "unknown" : Gender);
        }
    }

    private static bool IsTrivialDescription(string? description, string? name)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return true;
        }

        var normalizedDescription = NormalizeForComparison(description);
        if (string.IsNullOrWhiteSpace(normalizedDescription))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            var normalizedName = NormalizeForComparison(name);
            if (string.Equals(normalizedDescription, normalizedName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool DescriptionsEquivalent(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(
            NormalizeForComparison(left),
            NormalizeForComparison(right),
            StringComparison.Ordinal);
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
                AddVariants(alias, 3, 2);
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

    private static IEnumerable<string> BuildProfileKeys(ProfileAccumulator profile)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddKeys(profile.Name);
        foreach (var alias in profile.Aliases)
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

    private static DescriptionRefineResult? ParseRefineResponse(string content)
    {
        var json = JsonExtractor.ExtractJson(content);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (!root.TryGetProperty("update", out var updateElement))
            {
                return null;
            }

            var update = updateElement.ValueKind == JsonValueKind.True;
            var description = root.TryGetProperty("description", out var descElement) && descElement.ValueKind == JsonValueKind.String
                ? descElement.GetString()
                : null;

            return new DescriptionRefineResult(update, description);
        }
        catch
        {
            return null;
        }
    }

    private const int MinMatchScore = 2;

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

    private const string CharacterDescriptionRefinerSystemPrompt = """
You are CharacterDescriptionRefiner.

Input: JSON:
{
  "task": "refine_description",
  "current": { "name": "...", "description": "...", "gender": "...", "aliases": ["..."] },
  "candidate": { "canonicalName": "...", "description": "...", "gender": "...", "aliases": ["..."] }
}

Rules:
- Decide whether the candidate adds NEW factual information beyond the current description.
- If there are no new facts, return {"update": false}.
- If there are new facts, return {"update": true, "description": "..."}.
- description must be 1-3 sentences, coherent, and in the same language as the input.
- Use ONLY facts supported by current + candidate. Do not invent.
- When updating, integrate existing facts with the new ones; do not drop relevant details.
- Do not mention that this is an update.

Output:
Return JSON object only. No markdown. No extra text.
""";

    private const string CharacterExtractionSystemPrompt = """
You are CharacterExtractor.

Input: JSON:
{ "task": "extract_characters", "paragraphs": [ { "pointer": "...", "text": "..." } ] }

Rules:
- Identify only PEOPLE/CHARACTERS mentioned in the text.
- If a paragraph contains no character mentions, ignore it.
- Do not guess. If it is unclear that an entity is a person, skip it.
- Ignore generic groups/roles or unnamed speakers (e.g., "shorties", "listeners", "astronomers").
- Treat a role as a character only when it is part of a stable unique designation tied to a person (e.g., "Professor Zvezdochkin").
- Use ONLY the provided text. Do not invent facts.
- Use the same language as the evidence text.

For each character:
- canonicalName: normalized canonical name (nominative when applicable).
- aliases: other name forms found in the text (inflections, nicknames, short forms, surname-only, etc.).
  Do not repeat canonicalName. Keep unique.
- gender: "male" | "female" | "unknown". Use "unknown" if not supported by evidence.
- description: 1-3 sentences. Strictly grounded in evidence.

Output:
Return JSON ARRAY ONLY. No markdown. No extra text.
Schema:
[
  {
    "canonicalName": "...",
    "aliases": ["...", "..."],
    "gender": "male|female|unknown",
    "description": "..."
  }
]
""";
}

