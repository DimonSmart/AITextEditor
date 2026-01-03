using System.Text.Json;
using System.Text.RegularExpressions;
using AiTextEditor.Lib.Model;
using AiTextEditor.Lib.Services;
using AiTextEditor.Lib.Services.SemanticKernel;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace AiTextEditor.SemanticKernel;

public sealed class CharacterRosterGenerator
{
    private static readonly Regex NamePattern = new(@"[\p{Lu}][\p{L}\p{M}]+(?:[\s\-]+[\p{Lu}][\p{L}\p{M}]+){0,3}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IDocumentContext documentContext;
    private readonly CharacterRosterService rosterService;
    private readonly CursorAgentLimits limits;
    private readonly ILogger<CharacterRosterGenerator> logger;
    private readonly IChatCompletionService? chatService;

    public CharacterRosterGenerator(
        IDocumentContext documentContext,
        CharacterRosterService rosterService,
        CursorAgentLimits limits,
        ILogger<CharacterRosterGenerator> logger,
        IChatCompletionService? chatService = null)
    {
        this.documentContext = documentContext ?? throw new ArgumentNullException(nameof(documentContext));
        this.rosterService = rosterService ?? throw new ArgumentNullException(nameof(rosterService));
        this.limits = limits ?? throw new ArgumentNullException(nameof(limits));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.chatService = chatService;
    }

    public Task<CharacterRoster> GenerateAsync(CancellationToken cancellationToken = default)
    {
        return GenerateInternalAsync(includeDossiers: false, cancellationToken);
    }

    public Task<CharacterRoster> GenerateDossiersAsync(CancellationToken cancellationToken = default)
    {
        return GenerateInternalAsync(includeDossiers: true, cancellationToken);
    }

    public async Task<CharacterRoster> GenerateFromEvidenceAsync(
        IReadOnlyList<EvidenceItem> evidence,
        bool includeDossiers = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        cancellationToken.ThrowIfCancellationRequested();

        if (evidence.Count == 0)
        {
            return rosterService.GetRoster();
        }

        var candidates = evidence
            .Where(e => !string.IsNullOrWhiteSpace(e.Excerpt) && !string.IsNullOrWhiteSpace(e.Pointer))
            .SelectMany(e => ExtractCandidates(e.Excerpt, e.Pointer!))
            .ToList();

        if (candidates.Count == 0)
        {
            return rosterService.GetRoster();
        }

        var accumulators = BuildAccumulatorsFromCandidates(candidates, null);
        var roster = BuildRosterFromAccumulators(accumulators);
        if (includeDossiers)
        {
            roster = await EnrichWithDossiersAsync(roster, accumulators.Values.ToList(), preserveNonDossiers: true, cancellationToken);
        }

        rosterService.ReplaceRoster(roster.Characters);
        return rosterService.GetRoster();
    }

    public Task<CharacterRoster> RefreshAsync(IReadOnlyCollection<string>? changedPointers, CancellationToken cancellationToken = default)
    {
        return RefreshInternalAsync(changedPointers, includeDossiers: false, cancellationToken);
    }

    public Task<CharacterRoster> RefreshDossiersAsync(IReadOnlyCollection<string>? changedPointers, CancellationToken cancellationToken = default)
    {
        return RefreshInternalAsync(changedPointers, includeDossiers: true, cancellationToken);
    }

    private static List<CharacterCandidate> ExtractCandidates(LinearItem item)
    {
        if (item.Type == LinearItemType.Heading)
        {
            return [];
        }

        var text = string.IsNullOrWhiteSpace(item.Text) ? item.Markdown : item.Text;
        return ExtractCandidates(text, item.Pointer.ToCompactString());
    }

    private static List<CharacterCandidate> ExtractCandidates(string? text, string pointer)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(pointer))
        {
            return [];
        }

        var sentences = SplitSentences(text);
        var candidates = new List<CharacterCandidate>();

        for (var i = 0; i < sentences.Count; i++)
        {
            var sentence = sentences[i];
            if (string.IsNullOrWhiteSpace(sentence))
            {
                continue;
            }

            var matches = NamePattern.Matches(sentence);
            if (matches.Count == 0)
            {
                continue;
            }

            var context = BuildSentenceContext(sentences, i);
            foreach (Match match in matches)
            {
                if (!match.Success)
                {
                    continue;
                }

                var candidateName = NormalizeCandidateName(match.Value);
                if (string.IsNullOrWhiteSpace(candidateName))
                {
                    continue;
                }

                candidates.Add(new CharacterCandidate(candidateName, context, pointer));
            }
        }

        return candidates;
    }

    private static List<string> SplitSentences(string text)
    {
        var parts = text.Split(['.', '!', '?'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
    }

    private static string BuildSentenceContext(IReadOnlyList<string> sentences, int index)
    {
        var parts = new List<string>(3);

        if (index > 0 && !string.IsNullOrWhiteSpace(sentences[index - 1]))
        {
            parts.Add(sentences[index - 1].Trim());
        }

        var current = sentences[index].Trim();
        if (!string.IsNullOrWhiteSpace(current))
        {
            parts.Add(current);
        }

        if (index + 1 < sentences.Count && !string.IsNullOrWhiteSpace(sentences[index + 1]))
        {
            parts.Add(sentences[index + 1].Trim());
        }

        var combined = string.Join(". ", parts);
        return TrimEvidence(combined);
    }

    private static string TrimEvidence(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length <= DefaultMaxEvidenceLength)
        {
            return trimmed;
        }

        return trimmed[..DefaultMaxEvidenceLength].TrimEnd() + "...";
    }

    private static Dictionary<string, CharacterAccumulator> BuildAccumulatorsFromCandidates(IEnumerable<CharacterCandidate> candidates, CharacterRoster? existingRoster)
    {
        var items = candidates.ToList();
        var existing = existingRoster?.Characters.ToDictionary(
            c => NormalizeName(c.Name),
            c => new CharacterAccumulator(c),
            StringComparer.OrdinalIgnoreCase) ?? new Dictionary<string, CharacterAccumulator>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in items)
        {
            var key = NormalizeName(candidate.Name);
            if (!existing.TryGetValue(key, out var accumulator))
            {
                accumulator = new CharacterAccumulator(candidate.Name, candidate.Phrase, candidate.Pointer);
                existing[key] = accumulator;
            }
            else
            {
                accumulator.RegisterCandidate(candidate);
            }
        }

        return existing;
    }

    private static string NormalizeName(string name)
    {
        var trimmed = name.Trim();
        return Regex.Replace(trimmed, "\\s+", " ").ToLowerInvariant();
    }

    private sealed record CharacterCandidate(string Name, string Phrase, string Pointer);

    private sealed class CharacterAccumulator
    {
        private readonly string? existingId;
        private readonly HashSet<string> aliases;
        private readonly List<CharacterCandidate> candidates = [];
        private readonly string gender;

        public CharacterAccumulator(string name, string phrase, string pointer)
        {
            CanonicalName = name;
            Description = phrase;
            FirstPointer = pointer;
            aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            gender = "unknown";
            if (!string.IsNullOrWhiteSpace(phrase))
            {
                candidates.Add(new CharacterCandidate(name, phrase, pointer));
            }
        }

        public CharacterAccumulator(CharacterProfile profile)
        {
            existingId = profile.CharacterId;
            CanonicalName = profile.Name;
            Description = profile.Description;
            FirstPointer = profile.FirstPointer;
            aliases = new HashSet<string>(profile.Aliases, StringComparer.OrdinalIgnoreCase);
            gender = profile.Gender;
        }

        public string CanonicalName { get; }

        public string Description { get; private set; }

        public string? FirstPointer { get; private set; }

        public IReadOnlyList<CharacterCandidate> Candidates => candidates;

        public void RegisterCandidate(CharacterCandidate candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate.Name))
            {
                return;
            }

            if (!string.Equals(candidate.Name, CanonicalName, StringComparison.OrdinalIgnoreCase) &&
                !IsInflectionAlias(CanonicalName, candidate.Name))
            {
                aliases.Add(candidate.Name.Trim());
            }

            if (string.IsNullOrWhiteSpace(Description) && !string.IsNullOrWhiteSpace(candidate.Phrase))
            {
                Description = candidate.Phrase;
            }

            if (string.IsNullOrWhiteSpace(FirstPointer) && !string.IsNullOrWhiteSpace(candidate.Pointer))
            {
                FirstPointer = candidate.Pointer;
            }

            if (!string.IsNullOrWhiteSpace(candidate.Phrase))
            {
                candidates.Add(candidate);
            }
        }

        public CharacterProfile ToProfile()
        {
            var id = string.IsNullOrWhiteSpace(existingId) ? Guid.NewGuid().ToString("N") : existingId;
            var description = string.IsNullOrWhiteSpace(Description) ? CanonicalName : Description;
            var aliasList = aliases
                .Where(a => !string.Equals(a, CanonicalName, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(a => a, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new CharacterProfile(
                id,
                CanonicalName,
                description,
                aliasList,
                FirstPointer,
                gender);
        }
    }

    private async Task<CharacterRoster> GenerateInternalAsync(bool includeDossiers, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var cursor = new FullScanCursorStream(documentContext.Document, limits.MaxElements, limits.MaxBytes, null, includeHeadings: true, logger);
        var candidates = new List<CharacterCandidate>();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var portion = cursor.NextPortion();
            if (portion.Items.Count == 0 && !portion.HasMore)
            {
                break;
            }

            foreach (var item in portion.Items)
            {
                candidates.AddRange(ExtractCandidates(item));
            }

            if (!portion.HasMore)
            {
                break;
            }
        }

        var accumulators = BuildAccumulatorsFromCandidates(candidates, null);
        var roster = BuildRosterFromAccumulators(accumulators);
        if (includeDossiers)
        {
            roster = await EnrichWithDossiersAsync(roster, accumulators.Values.ToList(), preserveNonDossiers: true, cancellationToken);
        }

        rosterService.ReplaceRoster(roster.Characters);
        return rosterService.GetRoster();
    }

    private async Task<CharacterRoster> RefreshInternalAsync(IReadOnlyCollection<string>? changedPointers, bool includeDossiers, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (changedPointers == null || changedPointers.Count == 0)
        {
            return includeDossiers
                ? await GenerateInternalAsync(includeDossiers: true, cancellationToken)
                : await GenerateInternalAsync(includeDossiers: false, cancellationToken);
        }

        var pointerSet = new HashSet<string>(changedPointers.Where(p => !string.IsNullOrWhiteSpace(p)), StringComparer.Ordinal);
        if (pointerSet.Count == 0)
        {
            return rosterService.GetRoster();
        }

        var lookup = documentContext.Document.Items.ToDictionary(i => i.Pointer.ToCompactString(), i => i, StringComparer.Ordinal);
        var candidates = new List<CharacterCandidate>();
        foreach (var pointer in pointerSet)
        {
            if (!lookup.TryGetValue(pointer, out var item))
            {
                logger.LogWarning("RefreshCharacterRoster: pointer not found: {Pointer}", pointer);
                continue;
            }

            candidates.AddRange(ExtractCandidates(item));
        }

        var accumulators = BuildAccumulatorsFromCandidates(candidates, rosterService.GetRoster());
        var roster = BuildRosterFromAccumulators(accumulators);
        if (includeDossiers)
        {
            roster = await EnrichWithDossiersAsync(roster, accumulators.Values.ToList(), preserveNonDossiers: true, cancellationToken);
        }

        rosterService.ReplaceRoster(roster.Characters);
        return rosterService.GetRoster();
    }

    private static CharacterRoster BuildRosterFromAccumulators(Dictionary<string, CharacterAccumulator> accumulators)
    {
        var profiles = accumulators.Values
            .Select(acc => acc.ToProfile())
            .ToList();

        return new CharacterRoster(Guid.NewGuid().ToString("N"), 1, profiles);
    }

    private async Task<CharacterRoster> EnrichWithDossiersAsync(
        CharacterRoster roster,
        IReadOnlyList<CharacterAccumulator> accumulators,
        bool preserveNonDossiers,
        CancellationToken cancellationToken)
    {
        if (chatService == null)
        {
            logger.LogWarning("CharacterRoster: chat service not configured, skipping dossier generation.");
            return roster;
        }

        var evidence = CollectEvidence(accumulators);
        var summaries = await SummarizeWithLlmAsync(evidence, cancellationToken);

        if (summaries.Count == 0)
        {
            return roster;
        }

        var updated = roster.Characters
            .Select(profile =>
            {
                var key = NormalizeName(profile.Name);
                if (summaries.TryGetValue(key, out var summary))
                {
                    var canonicalName = string.IsNullOrWhiteSpace(summary.CanonicalName) ? profile.Name : summary.CanonicalName.Trim();
                    var description = string.IsNullOrWhiteSpace(summary.Description) ? profile.Description : summary.Description.Trim();
                    var gender = string.IsNullOrWhiteSpace(summary.Gender) ? profile.Gender : summary.Gender.Trim();
                    var aliases = MergeAliases(profile, canonicalName);

                    return profile with
                    {
                        Name = canonicalName,
                        Description = description,
                        Gender = gender,
                        Aliases = aliases
                    };
                }

                return preserveNonDossiers ? profile : null;
            })
            .Where(profile => profile != null)
            .Select(profile => profile!)
            .ToList();

        return roster with { Characters = updated };
    }

    private static IReadOnlyList<string> MergeAliases(CharacterProfile profile, string canonicalName)
    {
        if (string.Equals(profile.Name, canonicalName, StringComparison.OrdinalIgnoreCase))
        {
            return profile.Aliases;
        }

        var aliases = profile.Aliases
            .Append(profile.Name)
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Where(a => !IsInflectionAlias(canonicalName, a))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(a => a, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return aliases;
    }

    private static bool IsInflectionAlias(string canonicalName, string alias)
    {
        if (string.IsNullOrWhiteSpace(canonicalName) || string.IsNullOrWhiteSpace(alias))
        {
            return false;
        }

        var trimmedCanonical = canonicalName.Trim();
        var trimmedAlias = alias.Trim();
        if (string.Equals(trimmedCanonical, trimmedAlias, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (trimmedCanonical.Contains(' ') || trimmedCanonical.Contains('-') ||
            trimmedAlias.Contains(' ') || trimmedAlias.Contains('-'))
        {
            return false;
        }

        if (!IsLikelyCyrillicWord(trimmedCanonical) || !IsLikelyCyrillicWord(trimmedAlias))
        {
            return false;
        }

        var normalizedCanonical = NormalizeCyrillic(trimmedCanonical);
        var normalizedAlias = NormalizeCyrillic(trimmedAlias);

        var stemCanonical = StripRussianEnding(normalizedCanonical);
        var stemAlias = StripRussianEnding(normalizedAlias);

        if (stemCanonical.Length < 3 || stemAlias.Length < 3)
        {
            return false;
        }

        return string.Equals(stemCanonical, stemAlias, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLikelyCyrillicWord(string word)
    {
        foreach (var ch in word)
        {
            if (!char.IsLetter(ch))
            {
                continue;
            }

            if (ch < '\u0400' || ch > '\u052F')
            {
                return false;
            }
        }

        return true;
    }

    private static string NormalizeCyrillic(string text)
    {
        return text.Trim().ToLowerInvariant().Replace('ё', 'е');
    }

    private static string StripRussianEnding(string word)
    {
        foreach (var ending in RussianCaseEndings)
        {
            if (word.EndsWith(ending, StringComparison.Ordinal) && word.Length - ending.Length >= 3)
            {
                return word[..^ending.Length];
            }
        }

        return word;
    }

    private List<CharacterEvidence> CollectEvidence(IEnumerable<CharacterAccumulator> accumulators)
    {
        var evidence = accumulators
            .Where(acc => acc.Candidates.Count > 0)
            .Where(acc => IsLikelyCharacterName(acc.CanonicalName))
            .Select(acc => new CharacterEvidence(
                acc.CanonicalName,
                acc.Candidates
                    .Where(c => !string.IsNullOrWhiteSpace(c.Phrase))
                    .DistinctBy(c => $"{c.Pointer}|{c.Phrase}")
                    .ToList()))
            .Where(ev => ev.Mentions.Count >= DefaultMinMentions || ev.Mentions.Any(m => m.Phrase.Length >= DefaultMinMentionLength))
            .OrderByDescending(ev => ev.Mentions.Count)
            .Select(ev => ev with { Mentions = ev.Mentions.Take(DefaultMaxEvidencePerCharacter).ToList() });

        var maxCharacters = limits.CharacterRosterMaxCharacters;
        if (maxCharacters.HasValue && maxCharacters.Value > 0)
        {
            evidence = evidence.Take(maxCharacters.Value);
        }

        return evidence.ToList();
    }

    private async Task<Dictionary<string, CharacterDossierSummary>> SummarizeWithLlmAsync(
        List<CharacterEvidence> evidence,
        CancellationToken cancellationToken)
    {
        if (evidence.Count == 0)
        {
            return new Dictionary<string, CharacterDossierSummary>(StringComparer.OrdinalIgnoreCase);
        }

        var payload = new
        {
            task = "character_dossiers",
            characters = evidence.Select(ev => new
            {
                inputName = ev.Name,
                mentions = ev.Mentions.Select(m => new { pointer = m.Pointer, sentence = m.Phrase })
            })
        };

        var prompt = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = false
        });

        var history = new ChatHistory();
        history.AddSystemMessage(DossierSystemPrompt);
        history.AddUserMessage(prompt);

        var settings = new OpenAIPromptExecutionSettings
        {
            Temperature = 0,
            MaxTokens = 1200
        };

        var service = chatService ?? throw new InvalidOperationException("Chat service not configured.");
        var response = await service.GetChatMessageContentsAsync(history, settings, cancellationToken: cancellationToken);
        var content = response.FirstOrDefault()?.Content ?? string.Empty;
        var json = ExtractJson(content);

        if (string.IsNullOrWhiteSpace(json))
        {
            logger.LogWarning("Character dossier generation returned empty JSON.");
            return new Dictionary<string, CharacterDossierSummary>(StringComparer.OrdinalIgnoreCase);
        }

        return ParseDossierJson(json);
    }

    private static Dictionary<string, CharacterDossierSummary> ParseDossierJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var result = new Dictionary<string, CharacterDossierSummary>(StringComparer.OrdinalIgnoreCase);

            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in root.EnumerateArray())
                {
                    TryReadDossier(entry, result);
                }

                return result;
            }

            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("characters", out var characters) && characters.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in characters.EnumerateArray())
                    {
                        TryReadDossier(entry, result);
                    }
                }
            }

            return result;
        }
        catch
        {
            return new Dictionary<string, CharacterDossierSummary>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void TryReadDossier(JsonElement entry, Dictionary<string, CharacterDossierSummary> result)
    {
        if (entry.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var inputName = ReadStringProperty(entry, "inputName")
            ?? ReadStringProperty(entry, "name");
        if (string.IsNullOrWhiteSpace(inputName))
        {
            return;
        }

        var description = ReadStringProperty(entry, "description");
        if (string.IsNullOrWhiteSpace(description))
        {
            return;
        }

        var canonicalName = ReadStringProperty(entry, "canonicalName")
            ?? ReadStringProperty(entry, "canonical_name")
            ?? ReadStringProperty(entry, "normalizedName")
            ?? ReadStringProperty(entry, "normalized_name")
            ?? inputName;

        var gender = ReadStringProperty(entry, "gender")
            ?? ReadStringProperty(entry, "sex")
            ?? "unknown";

        var summary = new CharacterDossierSummary(
            inputName.Trim(),
            canonicalName.Trim(),
            gender.Trim(),
            description.Trim());
        result[NormalizeName(inputName)] = summary;
    }

    private static string? ReadStringProperty(JsonElement entry, string propertyName)
    {
        if (!entry.TryGetProperty(propertyName, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return element.GetString();
    }

    private static string? ExtractJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.Trim();
        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            return trimmed.Substring(start, end - start + 1);
        }

        start = trimmed.IndexOf('[');
        end = trimmed.LastIndexOf(']');
        if (start >= 0 && end > start)
        {
            return trimmed.Substring(start, end - start + 1);
        }

        return null;
    }

    private static bool IsLikelyCharacterName(string name)
    {
        var normalized = NormalizeName(name);
        if (normalized.Length < 3)
        {
            return false;
        }

        return !StopWords.Contains(normalized);
    }

    private static string? NormalizeCandidateName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var words = raw
            .Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        while (words.Count > 1 && StopWords.Contains(NormalizeName(words[0])))
        {
            words.RemoveAt(0);
        }

        if (words.Count == 0)
        {
            return null;
        }

        var candidate = string.Join(' ', words);
        var normalized = NormalizeName(candidate);
        if (normalized.Length < 3 || StopWords.Contains(normalized))
        {
            return null;
        }

        return candidate;
    }

    private sealed record CharacterEvidence(string Name, List<CharacterCandidate> Mentions);

    private sealed record CharacterDossierSummary(
        string InputName,
        string CanonicalName,
        string Gender,
        string Description);

    private const int DefaultMinMentions = 2;
    private const int DefaultMinMentionLength = 30;
    private const int DefaultMaxEvidencePerCharacter = 5;
    private const int DefaultMaxEvidenceLength = 240;

    private static readonly string[] RussianCaseEndings =
    [
        "иями",
        "ями",
        "ами",
        "ыми",
        "ими",
        "ого",
        "его",
        "ому",
        "ему",
        "ые",
        "ие",
        "ых",
        "их",
        "ая",
        "яя",
        "ое",
        "ее",
        "ым",
        "им",
        "ой",
        "ей",
        "ою",
        "ею",
        "ью",
        "ах",
        "ях",
        "ам",
        "ям",
        "ом",
        "ем",
        "а",
        "я",
        "е",
        "и",
        "ы",
        "у",
        "ю"
    ];

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "\u043e\u043d",
        "\u043e\u043d\u0430",
        "\u043e\u043d\u043e",
        "\u043e\u043d\u0438",
        "\u0435\u0433\u043e",
        "\u0435\u0435",
        "\u0435\u0451",
        "\u0435\u043c\u0443",
        "\u0435\u0439",
        "\u0438\u0445",
        "\u0438\u043c",
        "\u0438\u043c\u0438",
        "\u043c\u044b",
        "\u0432\u044b",
        "\u0442\u044b",
        "\u043c\u043d\u0435",
        "\u043c\u0435\u043d\u044f",
        "\u043d\u0430\u043c",
        "\u043d\u0430\u0441",
        "\u0432\u0430\u043c",
        "\u0432\u0430\u0441",
        "\u0442\u0435\u0431\u0435",
        "\u0442\u0435\u0431\u044f",
        "\u043c\u043e\u0439",
        "\u043c\u043e\u044f",
        "\u043c\u043e\u0435",
        "\u043c\u043e\u0438",
        "\u0442\u0432\u043e\u0439",
        "\u0442\u0432\u043e\u044f",
        "\u0442\u0432\u043e\u0435",
        "\u0442\u0432\u043e\u0438",
        "\u043d\u0430\u0448",
        "\u043d\u0430\u0448\u0430",
        "\u043d\u0430\u0448\u0435",
        "\u043d\u0430\u0448\u0438",
        "\u0432\u0430\u0448",
        "\u0432\u0430\u0448\u0430",
        "\u0432\u0430\u0448\u0435",
        "\u0432\u0430\u0448\u0438",
        "\u044d\u0442\u043e",
        "\u044d\u0442\u043e\u0442",
        "\u044d\u0442\u0430",
        "\u044d\u0442\u0438",
        "\u0442\u043e\u0442",
        "\u0442\u0430",
        "\u0442\u0435",
        "\u0442\u0443\u0442",
        "\u0442\u0430\u043c",
        "\u0437\u0434\u0435\u0441\u044c",
        "\u0442\u0443\u0434\u0430",
        "\u0441\u044e\u0434\u0430",
        "\u0442\u043e\u0433\u0434\u0430",
        "\u043f\u043e\u0442\u043e\u043c",
        "\u0441\u043d\u043e\u0432\u0430",
        "\u0432\u0441\u0435\u0433\u0434\u0430",
        "\u043a\u0442\u043e",
        "\u0447\u0442\u043e",
        "\u043a\u0430\u043a",
        "\u043a\u043e\u0433\u0434\u0430",
        "\u0433\u0434\u0435",
        "\u043f\u043e\u0447\u0435\u043c\u0443",
        "\u0437\u0430\u0447\u0435\u043c",
        "\u043a\u0443\u0434\u0430",
        "\u043e\u0442\u043a\u0443\u0434\u0430",
        "\u043d\u0435",
        "\u043d\u0438",
        "\u043d\u043e",
        "\u0434\u0430",
        "\u0438\u043b\u0438",
        "\u043b\u0438",
        "\u0436\u0435",
        "\u0431\u044b",
        "\u0432\u043e\u0442",
        "\u0442\u0430\u043a",
        "\u0442\u043e\u0436\u0435",
        "\u0442\u043e\u043b\u044c\u043a\u043e",
        "\u0443\u0436\u0435",
        "\u043d\u0430",
        "\u043f\u043e",
        "\u0434\u043e",
        "\u043e\u0442",
        "\u0438\u0437",
        "\u0437\u0430",
        "\u043f\u0440\u043e",
        "\u043f\u0440\u0438",
        "\u043f\u043e\u0434",
        "\u043d\u0430\u0434",
        "\u0434\u043b\u044f",
        "\u0431\u0435\u0437",
        "\u043e\u0431",
        "\u043e\u0431\u043e",
        "\u043e\u043a\u043e\u043b\u043e",
        "\u0447\u0435\u0440\u0435\u0437",
        "\u043c\u0435\u0436\u0434\u0443",
        "\u043f\u043e\u0441\u043b\u0435",
        "\u043f\u0435\u0440\u0435\u0434",
        "\u0432\u043e\u043a\u0440\u0443\u0433",
        "\u0441\u0440\u0435\u0434\u0438",
        "\u043a\u0440\u043e\u043c\u0435",
        "\u043f\u043e\u043a\u0430"
    };

    private const string DossierSystemPrompt = """
You are CharacterDossierBuilder. Use ONLY the provided evidence sentences.
For each entry, decide if the name refers to a person/character.
If it is a person, write a short dossier (1-2 sentences) describing appearance, role, habits, and personality when the evidence shows it.
If evidence is sparse but still indicates a person, write a minimal neutral description based only on the evidence.
If the name refers to a place, object, abstract concept, or the evidence is insufficient to tell it is a person, return an empty description.
Normalize the canonical name to nominative case. Identify gender as one of: male, female, unknown.
If gender is not explicit but the name form strongly implies it, infer gender from the name.
Use the same language as the evidence. Do NOT invent facts.

Return JSON ONLY. Schema:
{
  "characters": [
    {
      "inputName": "...",
      "canonicalName": "...",
      "gender": "male|female|unknown",
      "description": "..."
    }
  ]
}
""";
}
