using System.Text;
using System.Text.Json;
using AiTextEditor.Lib.Model;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using System.Linq;

namespace AiTextEditor.Lib.Services.SemanticKernel;

public sealed class CursorAgentRuntime
{
    private const int DefaultMaxSteps = 128;

    private readonly DocumentContext documentContext;
    private readonly TargetSetContext targetSetContext;
    private readonly IChatCompletionService chatService;
    private readonly ILogger<CursorAgentRuntime> logger;

    public CursorAgentRuntime(
        DocumentContext documentContext,
        TargetSetContext targetSetContext,
        IChatCompletionService chatService,
        ILogger<CursorAgentRuntime> logger)
    {
        this.documentContext = documentContext ?? throw new ArgumentNullException(nameof(documentContext));
        this.targetSetContext = targetSetContext ?? throw new ArgumentNullException(nameof(targetSetContext));
        this.chatService = chatService ?? throw new ArgumentNullException(nameof(chatService));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<CursorAgentResult> RunAsync(CursorAgentRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Mode == CursorAgentMode.CollectToTargetSet && string.IsNullOrWhiteSpace(request.TargetSetId))
        {
            throw new ArgumentException("TargetSetId is required for CollectToTargetSet mode.", nameof(request));
        }

        var maxSteps = request.MaxSteps.GetValueOrDefault(DefaultMaxSteps);
        var systemPrompt = BuildSystemPrompt(request.Mode);
        var taskMessage = BuildTaskMessage(request);
        string? lastAssistantMessage = null;
        string? lastPortionMessage = null;

        for (var step = 0; step < maxSteps; step++)
        {
            var command = await GetNextCommandAsync(systemPrompt, taskMessage, lastAssistantMessage, lastPortionMessage, cancellationToken, step);
            if (command == null)
            {
                lastAssistantMessage = null;
                lastPortionMessage = "Agent response malformed. Respond with JSON action.";
                continue;
            }

            if (TryComplete(request, command, out var result))
            {
                return result;
            }

            if (TryHandleAction(request, command, out var newPortionMessage))
            {
                lastAssistantMessage = command.RawContent;
                lastPortionMessage = newPortionMessage;
                continue;
            }

            lastAssistantMessage = command.RawContent;
            lastPortionMessage = "Unknown action. Use cursor_next, target_set_add, agent_finish_success, agent_finish_not_found.";
        }

        return new CursorAgentResult(false, "Max steps exceeded", null, null, request.TargetSetId);
    }

    private async Task<AgentCommand?> GetNextCommandAsync(string systemPrompt, string taskMessage, string? lastAssistantMessage, string? lastPortionMessage, CancellationToken cancellationToken, int step)
    {
        var history = new ChatHistory();
        history.AddSystemMessage(systemPrompt);
        history.AddUserMessage(taskMessage);
        if (!string.IsNullOrWhiteSpace(lastAssistantMessage))
        {
            history.AddAssistantMessage(lastAssistantMessage);
        }

        if (!string.IsNullOrWhiteSpace(lastPortionMessage))
        {
            history.AddUserMessage(lastPortionMessage);
        }

        var response = await chatService.GetChatMessageContentsAsync(history, CreateSettings(), cancellationToken: cancellationToken);
        var content = response.FirstOrDefault()?.Content ?? string.Empty;
        logger.LogDebug("Cursor agent step {Step}: {Response}", step, content);

        var parsed = ParseCommand(content);
        return parsed?.WithRawContent(content);
    }

    private bool TryHandleAction(CursorAgentRequest request, AgentCommand command, out string? portionMessage)
    {
        portionMessage = null;

        if (IsCursorNext(command.Action))
        {
            portionMessage = TryHandleCursorNext(request);
            return true;
        }

        if (IsTargetSetAdd(command.Action))
        {
            portionMessage = TryHandleTargetSetAdd(request, command);
            return true;
        }

        return false;
    }

    private static bool IsCursorNext(string action) => action.Equals("cursor_next", StringComparison.OrdinalIgnoreCase);

    private static bool IsTargetSetAdd(string action) => action.Equals("target_set_add", StringComparison.OrdinalIgnoreCase);

    private string? TryHandleCursorNext(CursorAgentRequest request)
    {
        var portion = documentContext.CursorContext.GetNextPortion(request.CursorName);
        if (portion == null)
        {
            return "cursor_next failed: cursor not found";
        }

        var snapshot = ProjectPortion(portion);
        return BuildPortionFeedback(snapshot);
    }

    private string? TryHandleTargetSetAdd(CursorAgentRequest request, AgentCommand command)
    {
        if (request.Mode != CursorAgentMode.CollectToTargetSet || string.IsNullOrWhiteSpace(request.TargetSetId))
        {
            return "target_set_add is not available in this mode.";
        }

        var indices = command.Indices ?? Array.Empty<int>();
        var added = targetSetContext.Add(request.TargetSetId!, indices);
        return added ? "target_set_add ok" : "target_set_add failed: unknown target set";
    }

    private static bool TryComplete(CursorAgentRequest request, AgentCommand command, out CursorAgentResult result)
    {
        if (IsFinishSuccess(command.Action))
        {
            result = BuildSuccess(request, command);
            return true;
        }

        if (IsFinishNotFound(command.Action))
        {
            result = new CursorAgentResult(false, command.Summary ?? "Not found", null, command.Summary, request.TargetSetId);
            return true;
        }

        result = default!;
        return false;
    }

    private static bool IsFinishSuccess(string action) => action.Equals("agent_finish_success", StringComparison.OrdinalIgnoreCase);

    private static bool IsFinishNotFound(string action) => action.Equals("agent_finish_not_found", StringComparison.OrdinalIgnoreCase);

    private static CursorAgentResult BuildSuccess(CursorAgentRequest request, AgentCommand command)
    {
        return request.Mode switch
        {
            CursorAgentMode.FirstMatch => new CursorAgentResult(true, null, command.FirstItemIndex, command.Summary, request.TargetSetId),
            CursorAgentMode.AggregateSummary => new CursorAgentResult(true, null, null, command.Summary, request.TargetSetId),
            CursorAgentMode.CollectToTargetSet => new CursorAgentResult(true, null, null, command.Summary, request.TargetSetId),
            _ => new CursorAgentResult(false, "Unsupported mode", null, null, request.TargetSetId)
        };
    }

    private static string BuildPortionFeedback(CursorPortionView portion)
    {
        var payload = new
        {
            cursor = portion.CursorName,
            hasMore = portion.HasMore,
            items = portion.Items.Select(item => new
            {
                pointer = item.Pointer,
                pointerLabel = item.PointerLabel,
                markdown = item.Markdown,
                type = item.Type
            })
        };

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });

        var builder = new StringBuilder();
        builder.AppendLine("cursor_next result (JSON):");
        builder.AppendLine(json);
        builder.AppendLine("Respond with a JSON action.");
        return builder.ToString();
    }

    private static string BuildTaskMessage(CursorAgentRequest request)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Cursor name: {request.CursorName}");
        builder.AppendLine($"Mode: {request.Mode}");
        if (!string.IsNullOrWhiteSpace(request.TargetSetId))
        {
            builder.AppendLine($"Target set: {request.TargetSetId}");
        }

        builder.AppendLine("Task description:");
        builder.AppendLine(request.TaskDescription);
        builder.AppendLine("Start by requesting cursor_next to receive items.");

        return builder.ToString();
    }

    private static string BuildSystemPrompt(CursorAgentMode mode)
    {
        var builder = new StringBuilder();
        builder.AppendLine("You are CursorAgent. Navigate the document via cursor_next and respond with a single JSON object only, no prose or code fences.");
        builder.AppendLine("Actions:");
        builder.AppendLine("- cursor_next: request the next items. Payload: {\"action\":\"cursor_next\"}");
        builder.AppendLine("- agent_finish_success: finish when done. Include summary. For FirstMatch also set firstItemIndex.");
        builder.AppendLine("- agent_finish_not_found: finish when the target cannot be found. Include summary explaining why.");
        builder.AppendLine("- target_set_add: only in CollectToTargetSet mode. Provide indices of relevant cursor items: {\"action\":\"target_set_add\",\"indices\":[...]}.");
        builder.AppendLine("Return format: {\"action\":\"<name\">,\"indices\":[...],\"firstItemIndex\":<number>,\"summary\":\"text\"}.");
        builder.AppendLine("When you find a match, include its pointerLabel (and pointer) in the summary so the user can locate it quickly.");
        builder.AppendLine("Stop as soon as you find the first relevant paragraph; avoid iterating the full cursor unnecessarily.");
        builder.AppendLine("Inspect each item in the current cursor_next batch independently. If multiple items are present, choose the earliest item whose markdown contains the target and return that item's index and pointer/pointerLabel.");
        builder.AppendLine("Prefer paragraph-like items (Type=Paragraph/ListItem) over headings unless the user explicitly asks about headings. Ignore heading matches when looking for mentions in the body text.");
        builder.AppendLine("Treat obvious Russian spelling/diacritic variants as equivalent (for example, ё and е) and match case-insensitively.");
        builder.AppendLine("Treat clear inflected forms of the same name/term as mentions (for example, профессор/профессора, Звёздочкин/Звёздочкина) unless the task demands an exact quote.");
        builder.AppendLine("Before finishing, explicitly check that the current markdown contains the normalized name/term; if not present, request cursor_next.");
        builder.AppendLine("When finishing, quote the exact matched substring from the markdown in the summary together with pointerLabel/pointer.");
        builder.AppendLine("Do not guess. Only finish when the current item's markdown actually contains the requested phrase/name/term after this normalization. If the text does not contain it, keep using cursor_next.");
        builder.AppendLine("Keep replies short. Avoid any text outside the JSON object.");
        builder.Append("Mode: ").Append(mode).Append(". Use only the allowed actions for this mode.");
        return builder.ToString();
    }

    private static CursorPortionView ProjectPortion(CursorPortion portion)
    {
        var items = portion.Items
            .Select(item => new CursorItemView(
                item.Index,
                item.Markdown,
                item.Pointer.Serialize(),
                item.Pointer.Label ?? $"{(item.Level.HasValue ? $"H{item.Level.Value}" : "H?")}.p{item.Index}",
                item.Type.ToString(),
                item.Level))
            .ToList();

        return new CursorPortionView(portion.CursorName, items, portion.HasMore);
    }

    private static AgentCommand? ParseCommand(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            var action = root.GetProperty("action").GetString();
            if (string.IsNullOrWhiteSpace(action))
            {
                return null;
            }

            var indices = root.TryGetProperty("indices", out var indicesElement) && indicesElement.ValueKind == JsonValueKind.Array
                ? indicesElement.EnumerateArray().Select(x => x.GetInt32()).ToList()
                : null;

            var summary = root.TryGetProperty("summary", out var summaryElement) && summaryElement.ValueKind != JsonValueKind.Null
                ? summaryElement.GetString()
                : null;

            var firstIndex = root.TryGetProperty("firstItemIndex", out var firstElement) && firstElement.ValueKind != JsonValueKind.Null
                ? firstElement.GetInt32()
                : (int?)null;

            return new AgentCommand(action!, indices, summary, firstIndex);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static OpenAIPromptExecutionSettings CreateSettings()
    {
        return new OpenAIPromptExecutionSettings
        {
            Temperature = 0,
            TopP = 0
        };
    }

    private sealed record AgentCommand(string Action, IReadOnlyList<int>? Indices, string? Summary, int? FirstItemIndex)
    {
        public string? RawContent { get; init; }

        public AgentCommand WithRawContent(string raw) => this with { RawContent = raw };
    }
}
