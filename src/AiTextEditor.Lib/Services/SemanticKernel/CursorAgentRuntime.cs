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
        var history = BuildHistory(request);

        for (var step = 0; step < maxSteps; step++)
        {
            var command = await GetNextCommandAsync(history, cancellationToken, step);
            if (command == null)
            {
                history.AddUserMessage("Agent response malformed. Respond with JSON action.");
                continue;
            }

            if (TryComplete(request, command, out var result))
            {
                return result;
            }

            if (TryHandleAction(request, command, history))
            {
                continue;
            }

            history.AddUserMessage("Unknown action. Use cursor_next, target_set_add, agent_finish_success, agent_finish_not_found.");
        }

        return new CursorAgentResult(false, "Max steps exceeded", null, null, request.TargetSetId);
    }

    private async Task<AgentCommand?> GetNextCommandAsync(ChatHistory history, CancellationToken cancellationToken, int step)
    {
        var response = await chatService.GetChatMessageContentsAsync(history, CreateSettings(), cancellationToken: cancellationToken);
        var content = response.FirstOrDefault()?.Content ?? string.Empty;
        logger.LogDebug("Cursor agent step {Step}: {Response}", step, content);

        return ParseCommand(content);
    }

    private bool TryHandleAction(CursorAgentRequest request, AgentCommand command, ChatHistory history)
    {
        if (IsCursorNext(command.Action))
        {
            return TryHandleCursorNext(request, history);
        }

        if (IsTargetSetAdd(command.Action))
        {
            return TryHandleTargetSetAdd(request, command, history);
        }

        return false;
    }

    private static bool IsCursorNext(string action) => action.Equals("cursor_next", StringComparison.OrdinalIgnoreCase);

    private static bool IsTargetSetAdd(string action) => action.Equals("target_set_add", StringComparison.OrdinalIgnoreCase);

    private bool TryHandleCursorNext(CursorAgentRequest request, ChatHistory history)
    {
        var portion = documentContext.CursorContext.GetNextPortion(request.CursorName);
        if (portion == null)
        {
            history.AddUserMessage("cursor_next failed: cursor not found");
            return true;
        }

        var snapshot = ProjectPortion(portion);
        history.AddUserMessage(BuildPortionFeedback(snapshot));
        return true;
    }

    private bool TryHandleTargetSetAdd(CursorAgentRequest request, AgentCommand command, ChatHistory history)
    {
        if (request.Mode != CursorAgentMode.CollectToTargetSet || string.IsNullOrWhiteSpace(request.TargetSetId))
        {
            history.AddUserMessage("target_set_add is not available in this mode.");
            return true;
        }

        var indices = command.Indices ?? Array.Empty<int>();
        var added = targetSetContext.Add(request.TargetSetId!, indices);
        history.AddUserMessage(added ? "target_set_add ok" : "target_set_add failed: unknown target set");
        return true;
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
        var builder = new StringBuilder();
        builder.AppendLine("cursor_next result:");
        builder.AppendLine($"hasMore: {portion.HasMore.ToString().ToLowerInvariant()}");
        builder.AppendLine("items:");

        foreach (var item in portion.Items)
        {
            builder.AppendLine($"- index: {item.Index}");
            builder.AppendLine($"  markdown: {item.Markdown}");
        }

        builder.AppendLine("Respond with a JSON action.");
        return builder.ToString();
    }

    private ChatHistory BuildHistory(CursorAgentRequest request)
    {
        var history = new ChatHistory();
        history.AddSystemMessage(BuildSystemPrompt(request.Mode));

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

        history.AddUserMessage(builder.ToString());
        return history;
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
        builder.AppendLine("Keep replies short. Avoid any text outside the JSON object.");
        builder.Append("Mode: ").Append(mode).Append(". Use only the allowed actions for this mode.");
        return builder.ToString();
    }

    private static CursorPortionView ProjectPortion(CursorPortion portion)
    {
        var items = portion.Items
            .Select(item => new CursorItemView(item.Index, item.Markdown))
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

    private sealed record AgentCommand(string Action, IReadOnlyList<int>? Indices, string? Summary, int? FirstItemIndex);
}
