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
            var response = await chatService.GetChatMessageContentsAsync(history, CreateSettings(), cancellationToken: cancellationToken);
            var content = response.FirstOrDefault()?.Content ?? string.Empty;
            logger.LogDebug("Cursor agent step {Step}: {Response}", step, content);

            var command = ParseCommand(content);
            if (command == null)
            {
                history.AddUserMessage("Agent response malformed. Respond with JSON action.");
                continue;
            }

            switch (command.Action.ToLowerInvariant())
            {
                case "cursor_next":
                    var portion = documentContext.CursorContext.GetNextPortion(request.CursorName);
                    if (portion == null)
                    {
                        history.AddUserMessage("cursor_next failed: cursor not found");
                        continue;
                    }

                    var snapshot = ProjectPortion(portion);
                    history.AddUserMessage(BuildPortionFeedback(snapshot));
                    break;

                case "target_set_add":
                    if (request.Mode != CursorAgentMode.CollectToTargetSet || string.IsNullOrWhiteSpace(request.TargetSetId))
                    {
                        history.AddUserMessage("target_set_add is not available in this mode.");
                        continue;
                    }

                    var added = targetSetContext.Add(request.TargetSetId!, command.Indices ?? Array.Empty<int>());
                    history.AddUserMessage(added ? "target_set_add ok" : "target_set_add failed: unknown target set");
                    break;

                case "agent_finish_success":
                    return BuildSuccess(request, command);

                case "agent_finish_not_found":
                    return new CursorAgentResult(false, command.Summary ?? "Not found", null, command.Summary, request.TargetSetId);

                default:
                    history.AddUserMessage("Unknown action. Use cursor_next, target_set_add, agent_finish_success, agent_finish_not_found.");
                    break;
            }
        }

        return new CursorAgentResult(false, "Max steps exceeded", null, null, request.TargetSetId);
    }

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
        builder.AppendLine("You are CursorAgent. Navigate a document via cursor_next calls and respond with compact JSON only.");
        builder.AppendLine("Available actions: cursor_next, target_set_add (CollectToTargetSet only), agent_finish_success, agent_finish_not_found.");
        builder.AppendLine("Return format: {\"action\":\"<name>\",\"indices\":[...],\"firstItemIndex\":<number>,\"summary\":\"text\"}.");
        builder.AppendLine("Keep replies short. Do not add explanations.");
        builder.Append("Mode: ").Append(mode).Append('.');
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
