using System.Text.Json;
using System.Linq;
using AiTextEditor.Lib.Common;
using AiTextEditor.Lib.Model;
using AiTextEditor.Lib.Services;
using AiTextEditor.Lib.Services.SemanticKernel;
using DimonSmart.AiUtils;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace AiTextEditor.SemanticKernel;

public sealed class CharacterRosterCursorOrchestrator
{
    private readonly IDocumentContext documentContext;
    private readonly ICursorStore cursorStore;
    private readonly CharacterRosterService rosterService;
    private readonly CursorAgentLimits limits;
    private readonly IChatCompletionService chatService;
    private readonly ILogger<CharacterRosterCursorOrchestrator> logger;
    private int cursorCounter;

    public CharacterRosterCursorOrchestrator(
        IDocumentContext documentContext,
        ICursorStore cursorStore,
        CharacterRosterService rosterService,
        IChatCompletionService chatService,
        CursorAgentLimits limits,
        ILogger<CharacterRosterCursorOrchestrator> logger)
    {
        this.documentContext = documentContext ?? throw new ArgumentNullException(nameof(documentContext));
        this.cursorStore = cursorStore ?? throw new ArgumentNullException(nameof(cursorStore));
        this.rosterService = rosterService ?? throw new ArgumentNullException(nameof(rosterService));
        this.chatService = chatService ?? throw new ArgumentNullException(nameof(chatService));
        this.limits = limits ?? throw new ArgumentNullException(nameof(limits));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<CharacterRoster> BuildRosterAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var cursorName = CreateCursorName();
        var cursor = new FullScanCursorStream(
            documentContext.Document,
            limits.MaxElements,
            limits.MaxBytes,
            null,
            includeHeadings: true,
            logger);

        if (!cursorStore.TryAddCursor(cursorName, cursor))
        {
            throw new InvalidOperationException($"cursor_register_failed: {cursorName}");
        }

        logger.LogInformation("CharacterRosterCursorOrchestrator: cursor created {Cursor}", cursorName);

        // Legacy agent-driven roster builder is temporarily disabled in favor of direct cursor scanning with tool calls.
        return await UpdateRosterFromCursorAsync(cursorName, cancellationToken);
    }

    public async Task<CharacterRoster> UpdateRosterFromCursorAsync(string cursorName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!cursorStore.TryGetCursor(cursorName, out var cursor) || cursor is null)
        {
            throw new InvalidOperationException($"cursor_not_found: {cursorName}");
        }

        var kernel = CreateKernel();
        var executionSettings = new OpenAIPromptExecutionSettings
        {
            ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions,
            Temperature = 0,
            MaxTokens = 1600
        };

        var stepsUsed = 0;
        var maxSteps = Math.Clamp(limits.DefaultMaxSteps, 1, limits.MaxStepsLimit);

        for (var step = 0; step < maxSteps; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var portion = cursor.NextPortion();
            if (portion.Items.Count == 0 && !portion.HasMore)
            {
                break;
            }

            var paragraphs = portion.Items
                .Where(item => item.Type != LinearItemType.Heading && !string.IsNullOrWhiteSpace(item.Markdown))
                .Select(item => (Pointer: item.Pointer.ToCompactString(), Text: item.Markdown))
                .ToList();

            if (paragraphs.Count > 0)
            {
                await ProcessPortionAsync(cursorName, paragraphs, executionSettings, kernel, cancellationToken);
            }

            stepsUsed = step + 1;

            if (!portion.HasMore)
            {
                break;
            }
        }

        if (stepsUsed >= maxSteps)
        {
            logger.LogWarning("CharacterRosterCursorOrchestrator: max steps reached {Steps}", stepsUsed);
        }

        return rosterService.GetRoster();
    }

    private Kernel CreateKernel()
    {
        var builder = Kernel.CreateBuilder();
        builder.Services.AddSingleton(chatService);
        builder.Services.AddSingleton(rosterService);

        var kernel = builder.Build();
        var tools = new CharacterRosterFunctionCollection(rosterService);
        kernel.Plugins.AddFromObject(tools, "character_roster_tools");
        return kernel;
    }

    private async Task ProcessPortionAsync(
        string cursorName,
        IReadOnlyList<(string Pointer, string Text)> paragraphs,
        OpenAIPromptExecutionSettings settings,
        Kernel kernel,
        CancellationToken cancellationToken)
    {
        var history = new ChatHistory();
        history.AddSystemMessage(CharacterExtractionSystemPrompt);

        var payload = new
        {
            task = "extract_characters_from_cursor",
            cursor = cursorName,
            paragraphs = paragraphs.Select(p => new { pointer = p.Pointer, text = p.Text })
        };

        history.AddUserMessage(JsonSerializer.Serialize(payload, SerializationOptions.RelaxedCompact));

        var response = await chatService.GetChatMessageContentsAsync(history, settings, kernel, cancellationToken);
        var content = response.FirstOrDefault()?.Content ?? string.Empty;

        var parsed = ParseCharacters(content);
        if (parsed.Count == 0)
        {
            return;
        }

        foreach (var candidate in parsed)
        {
            UpsertCandidate(candidate);
        }
    }

    private List<LlmCharacter> ParseCharacters(string content)
    {
        var json = JsonExtractor.ExtractJson(content);
        if (string.IsNullOrWhiteSpace(json))
        {
            logger.LogDebug("Character roster cursor: no JSON payload detected.");
            return [];
        }

        try
        {
            var items = JsonSerializer.Deserialize<List<LlmCharacter>>(json, SerializationOptions.RelaxedCompact) ?? [];
            return items
                .Select(NormalizeHit)
                .Where(x => !string.IsNullOrWhiteSpace(x.CanonicalName))
                .ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Character roster cursor: failed to parse model response.");
            return [];
        }
    }

    private void UpsertCandidate(LlmCharacter candidate)
    {
        var matches = FindMatches(candidate);
        var preferred = matches.Count == 1 ? matches[0] : matches.FirstOrDefault(m => string.Equals(m.Name, candidate.CanonicalName, StringComparison.OrdinalIgnoreCase));

        var characterId = preferred?.CharacterId ?? Guid.NewGuid().ToString("N");
        var name = string.IsNullOrWhiteSpace(preferred?.Name) ? candidate.CanonicalName!.Trim() : preferred!.Name;
        var description = SelectDescription(preferred?.Description, candidate.Description, name);
        var gender = ChooseGender(preferred?.Gender, candidate.Gender);
        var aliases = CollectAliases(preferred?.Aliases, name, candidate.Aliases);

        rosterService.UpdateCharacter(characterId, name, description, gender, aliases);
    }

    private List<CharacterProfile> FindMatches(LlmCharacter candidate)
    {
        var matches = new List<CharacterProfile>();
        if (!string.IsNullOrWhiteSpace(candidate.CanonicalName))
        {
            matches.AddRange(rosterService.FindByNameOrAlias(candidate.CanonicalName!));
        }

        if (candidate.Aliases is { Count: > 0 })
        {
            foreach (var alias in candidate.Aliases)
            {
                matches.AddRange(rosterService.FindByNameOrAlias(alias));
            }
        }

        return matches
            .DistinctBy(m => m.CharacterId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string SelectDescription(string? current, string? candidate, string fallbackName)
    {
        if (!string.IsNullOrWhiteSpace(candidate))
        {
            return candidate.Trim();
        }

        if (!string.IsNullOrWhiteSpace(current))
        {
            return current.Trim();
        }

        return fallbackName;
    }

    private static string ChooseGender(string? current, string? candidate)
    {
        var normalizedCandidate = NormalizeGender(candidate);
        if (!string.Equals(normalizedCandidate, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            return normalizedCandidate;
        }

        return NormalizeGender(current);
    }

    private static IReadOnlyCollection<string> CollectAliases(
        IReadOnlyList<string>? existing,
        string canonicalName,
        IReadOnlyCollection<string>? candidateAliases)
    {
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (existing is not null)
        {
            foreach (var alias in existing)
            {
                aliases.Add(alias);
            }
        }

        if (candidateAliases is not null)
        {
            foreach (var alias in candidateAliases)
            {
                aliases.Add(alias);
            }
        }

        aliases.RemoveWhere(a => string.Equals(a, canonicalName, StringComparison.OrdinalIgnoreCase));
        return aliases.ToList();
    }

    private string CreateCursorName() => $"roster_cursor_{cursorCounter++}";

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

    private sealed record LlmCharacter(string? CanonicalName, List<string>? Aliases, string? Gender, string? Description);

    private const string CharacterExtractionSystemPrompt = """
You are an expert at extracting and maintaining detailed character dossiers from Russian fiction.
For each provided paragraph batch, identify every distinct character mention and immediately invoke the provided tools to:
- locate existing characters by name or alias,
- read character details by id,
- update or create characters with consolidated canonical name, aliases, gender, and description.

Priorities:
- Always preserve the canonical name if it is already known.
- Use aliases to merge duplicate mentions.
- Keep descriptions concise but informative (one sentence).
- If uncertain about gender, keep it as "unknown".
""";
}
