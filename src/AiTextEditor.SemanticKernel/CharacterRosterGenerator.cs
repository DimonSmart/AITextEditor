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
        var index = ProfileAccumulator.CreateIndex(baseRoster);

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
                var hits = await ExtractCharactersWithLlmAsync(paragraphs, cancellationToken);
                ProfileAccumulator.Merge(index, hits);
            }

            if (!portion.HasMore)
            {
                break;
            }
        }

        var roster = ProfileAccumulator.ToRoster(baseRoster, index, limits.CharacterRosterMaxCharacters);
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

        var hits = await ExtractCharactersWithLlmAsync(paragraphs, cancellationToken);

        var baseRoster = rosterService.GetRoster();
        var index = ProfileAccumulator.CreateIndex(baseRoster);
        ProfileAccumulator.Merge(index, hits);

        var roster = ProfileAccumulator.ToRoster(baseRoster, index, limits.CharacterRosterMaxCharacters);
        rosterService.ReplaceRoster(roster.Characters);
        return rosterService.GetRoster();
    }

    public async Task<CharacterRoster> GenerateFromEvidenceAsync(
        IReadOnlyList<EvidenceItem> evidence,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        cancellationToken.ThrowIfCancellationRequested();

        var paragraphs = evidence
            .Where(e => !string.IsNullOrWhiteSpace(e.Pointer) && !string.IsNullOrWhiteSpace(e.Excerpt))
            .Select(e => (Pointer: e.Pointer!.Trim(), Text: e.Excerpt!.Trim()))
            .Where(p => p.Pointer.Length > 0 && p.Text.Length > 0)
            .ToList();

        if (paragraphs.Count == 0)
        {
            return rosterService.GetRoster();
        }

        var hits = await ExtractCharactersWithLlmAsync(paragraphs, cancellationToken);

        var baseRoster = rosterService.GetRoster();
        var index = ProfileAccumulator.CreateIndex(baseRoster);
        ProfileAccumulator.Merge(index, hits);

        var roster = ProfileAccumulator.ToRoster(baseRoster, index, limits.CharacterRosterMaxCharacters);
        rosterService.ReplaceRoster(roster.Characters);
        return rosterService.GetRoster();
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

        return hit with
        {
            CanonicalName = canonical,
            Aliases = aliases,
            Description = hit.Description?.Trim(),
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

    private sealed class ProfileAccumulator
    {
        private readonly string id;
        private readonly HashSet<string> aliases = new(StringComparer.OrdinalIgnoreCase);

        private string name;
        private string description;
        private string gender;

        private ProfileAccumulator(CharacterProfile profile)
        {
            id = profile.CharacterId;
            name = profile.Name;
            description = profile.Description;
            gender = string.IsNullOrWhiteSpace(profile.Gender) ? "unknown" : profile.Gender;

            foreach (var a in profile.Aliases)
            {
                if (!string.IsNullOrWhiteSpace(a) && !string.Equals(a, name, StringComparison.OrdinalIgnoreCase))
                {
                    aliases.Add(a.Trim());
                }
            }
        }

        private ProfileAccumulator(string canonicalName)
        {
            id = Guid.NewGuid().ToString("N");
            name = canonicalName.Trim();
            description = name;
            gender = "unknown";
        }

        public static Dictionary<string, ProfileAccumulator> CreateIndex(CharacterRoster roster)
        {
            return roster.Characters.ToDictionary(
                c => NormalizeKey(c.Name),
                c => new ProfileAccumulator(c),
                StringComparer.OrdinalIgnoreCase);
        }

        public static void Merge(Dictionary<string, ProfileAccumulator> index, IReadOnlyList<LlmCharacter> hits)
        {
            foreach (var hit in hits)
            {
                var canonical = hit.CanonicalName?.Trim();
                if (string.IsNullOrWhiteSpace(canonical))
                {
                    continue;
                }

                var key = NormalizeKey(canonical);
                if (!index.TryGetValue(key, out var a))
                {
                    a = new ProfileAccumulator(canonical);
                    index[key] = a;
                }

                a.Merge(hit);
            }
        }

        public static CharacterRoster ToRoster(
            CharacterRoster baseRoster,
            Dictionary<string, ProfileAccumulator> index,
            int? maxCharacters)
        {
            var profiles = index.Values
                .Select(x => x.ToProfile())
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (maxCharacters.HasValue && maxCharacters.Value > 0 && profiles.Count > maxCharacters.Value)
            {
                profiles = profiles.Take(maxCharacters.Value).ToList();
            }

            return baseRoster with { Characters = profiles };
        }

        private void Merge(LlmCharacter hit)
        {
            if (!string.IsNullOrWhiteSpace(hit.CanonicalName))
            {
                name = hit.CanonicalName.Trim();
            }

            if (hit.Aliases != null)
            {
                foreach (var a in hit.Aliases)
                {
                    if (string.IsNullOrWhiteSpace(a))
                    {
                        continue;
                    }

                    var trimmed = a.Trim();
                    if (!string.Equals(trimmed, name, StringComparison.OrdinalIgnoreCase))
                    {
                        aliases.Add(trimmed);
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(hit.Description) && (string.IsNullOrWhiteSpace(description) || description == name))
            {
                description = hit.Description.Trim();
            }

            if (!string.IsNullOrWhiteSpace(hit.Gender) && string.Equals(gender, "unknown", StringComparison.OrdinalIgnoreCase))
            {
                gender = hit.Gender.Trim();
            }
        }

        private CharacterProfile ToProfile()
        {
            var aliasList = aliases
                .Where(a => !string.Equals(a, name, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(a => a, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var desc = string.IsNullOrWhiteSpace(description) ? name : description;

            return new CharacterProfile(
                id,
                name,
                desc,
                aliasList,
                string.IsNullOrWhiteSpace(gender) ? "unknown" : gender);
        }

        private static string NormalizeKey(string value)
            => string.Join(' ', value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    private const string CharacterExtractionSystemPrompt = """
You are CharacterExtractor.

Input: JSON:
{ "task": "extract_characters", "paragraphs": [ { "pointer": "...", "text": "..." } ] }

Rules:
- Identify only PEOPLE/CHARACTERS mentioned in the text.
- If a paragraph contains no character mentions, ignore it.
- Do not guess. If it is unclear that an entity is a person, skip it.
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
