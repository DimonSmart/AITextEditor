using AiTextEditor.Lib.Model;
using DimonSmart.AiUtils;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiTextEditor.Lib.Services.SemanticKernel;

public sealed class CursorAgentRuntime
{
    internal const int DefaultMaxSteps = 128;
    internal const int MaxStepsLimit = 512;

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

        var requestedSteps = request.MaxSteps.GetValueOrDefault(DefaultMaxSteps);
        var maxSteps = requestedSteps > MaxStepsLimit ? MaxStepsLimit : requestedSteps;
        var systemPrompt = BuildSystemPrompt(request.Mode);
        var taskMessage = BuildTaskMessage(request);
        string? lastPortionMessage = null;
        var completedCursors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var step = 0; step < maxSteps; step++)
        {
            var command = await GetNextCommandAsync(systemPrompt, taskMessage, lastPortionMessage, cancellationToken, step);
            if (command == null)
            {
                lastPortionMessage = "Agent response malformed. Respond with JSON action.";
                continue;
            }

            if (TryComplete(request, command, out var result))
            {
                return result;
            }

            if (TryHandleAction(request, command, completedCursors, out var newPortionMessage))
            {
                lastPortionMessage = newPortionMessage;
                continue;
            }

            lastPortionMessage = "Unknown action. Use cursor_next, target_set_add, agent_finish_success, agent_finish_not_found.";
        }

        return new CursorAgentResult(false, "Max steps exceeded", null, null, request.TargetSetId);
    }

    private async Task<AgentCommand?> GetNextCommandAsync(string systemPrompt, string taskMessage, string? lastPortionMessage, CancellationToken cancellationToken, int step)
    {
        var history = new ChatHistory();
        history.AddSystemMessage(systemPrompt);
        history.AddUserMessage(taskMessage);

        if (!string.IsNullOrWhiteSpace(lastPortionMessage))
        {
            history.AddUserMessage(lastPortionMessage);
        }

        var response = await chatService.GetChatMessageContentsAsync(history, CreateSettings(), cancellationToken: cancellationToken);
        var message = response.FirstOrDefault();
        var content = message?.Content ?? string.Empty;
        LogCompletionSkeleton(step, message);
        LogRawCompletion(step, content);

        var parsed = ParseCommand(content, out var parsedFragment, out var multipleActions, out var finishDetected);
        if (multipleActions)
        {
            logger.LogWarning("multiple actions returned");
        }

        if (parsed != null)
        {
            logger.LogDebug("cursor_agent_parsed: step={Step}, action={Action}, finishFound={Finish}, parsedAction={ParsedAction}", step, parsed.Action, finishDetected, Truncate(parsedFragment ?? string.Empty, 500));
        }

        return parsed?.WithRawContent(parsedFragment ?? content);
    }

    private bool TryHandleAction(CursorAgentRequest request, AgentCommand command, HashSet<string> completedCursors, out string? portionMessage)
    {
        portionMessage = null;

        if (IsCursorNext(command.Action))
        {
            portionMessage = TryHandleCursorNext(request, completedCursors);
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

    private string? TryHandleCursorNext(CursorAgentRequest request, HashSet<string> completedCursors)
    {
        if (completedCursors.Contains(request.CursorName))
        {
            return $"cursor_next halted: cursor '{request.CursorName}' is complete. Finish with agent_finish_*";
        }

        var portion = documentContext.CursorContext.GetNextPortion(request.CursorName);
        if (portion == null)
        {
            return "cursor_next failed: cursor not found";
        }

        var snapshot = ProjectPortion(portion);
        var pointerLabel = snapshot.Items.FirstOrDefault()?.PointerLabel ?? "<none>";
        var snippet = snapshot.HasMore && snapshot.Items.Count > 0 ? Truncate(snapshot.Items[0].Markdown, 200) : string.Empty;
        var eventName = snapshot.HasMore ? "cursor_batch" : "cursor_batch_complete";
        logger.LogDebug("{Event}: cursor={Cursor}, count={Count}, hasMore={HasMore}, pointerLabel={PointerLabel}, snippet={Snippet}", eventName, snapshot.CursorName, snapshot.Items.Count, snapshot.HasMore, pointerLabel, snippet);

        if (!snapshot.HasMore)
        {
            completedCursors.Add(snapshot.CursorName);
        }

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
        if (!portion.HasMore)
        {
            builder.AppendLine("hasMore is false. Cursor stream is complete. Stop calling cursor_next.");
        }

        builder.AppendLine("Return only one JSON action.");
        return builder.ToString();
    }

    private static string BuildTaskMessage(CursorAgentRequest request)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Cursor: {request.CursorName}, mode: {request.Mode}.");
        builder.AppendLine($"Goal: {request.TaskDescription}");
        builder.AppendLine("Use one action per step. Start with cursor_next.");

        return builder.ToString();
    }

    private static string BuildSystemPrompt(CursorAgentMode mode)
    {
        var builder = new StringBuilder();
        builder.AppendLine("You are CursorAgent. Use exactly one JSON action per reply without code fences. Return only a single JSON action.");
        builder.AppendLine("Actions: cursor_next; agent_finish_success; agent_finish_not_found; target_set_add (CollectToTargetSet mode only).");
        builder.AppendLine("Choose the earliest matching item in the batch. Include pointerLabel and pointer in summaries.");
        builder.AppendLine("Stop after the first relevant paragraph. If text does not contain the normalized term, keep requesting cursor_next.");
        builder.AppendLine("Mode: ").Append(mode).Append(". Return format: {\"action\":...,\"indices\":[...],\"firstItemIndex\":n,\"summary\":\"text\"}.");
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

    private AgentCommand? ParseCommand(string content, out string? parsedFragment, out bool multipleActions, out bool finishDetected)
    {
        parsedFragment = null;

        var commands = JsonExtractor
            .ExtractAllJsons(content)
            .Select(ParseSingle)
            .Where(command => command != null)
            .Cast<AgentCommand>()
            .ToList();

        if (commands.Count == 0)
        {
            multipleActions = false;
            finishDetected = false;
            return null;
        }

        multipleActions = commands.Count > 1;
        var finish = commands.FirstOrDefault(command => IsFinishSuccess(command.Action) || IsFinishNotFound(command.Action));
        finishDetected = finish != null;

        var selected = finish ?? commands[0];
        parsedFragment = selected.RawContent;
        return selected;
    }

    private static AgentCommand? ParseSingle(string content)
    {
        var command = TryParseJson(content);
        if (command != null)
        {
            return command;
        }

        var sanitized = SanitizeJson(content);
        if (sanitized != content)
        {
            return TryParseJson(sanitized);
        }

        return null;
    }

    private static AgentCommand? TryParseJson(string content)
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

            return new AgentCommand(action!, indices, summary, firstIndex) { RawContent = content };
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string SanitizeJson(string json)
    {
        return Regex.Replace(json, "\"(?:[^\"\\\\]|\\\\.)*\"", match =>
        {
            var value = match.Value;
            if (value.Contains('\n') || value.Contains('\r') || value.Contains('\t'))
            {
                return value
                    .Replace("\n", "\\n")
                    .Replace("\r", "\\r")
                    .Replace("\t", "\\t");
            }
            return value;
        });
    }

    private void LogCompletionSkeleton(int step, object? message)
    {
        if (message == null)
        {
            logger.LogInformation("cursor_agent_call: step={Step}, callId=<none>, model=<unknown>, tokens=<unknown>, result=<empty>", step);
            return;
        }

        var metadata = message.GetType().GetProperty("Metadata")?.GetValue(message) as IReadOnlyDictionary<string, object?>;
        var modelId = message.GetType().GetProperty("ModelId")?.GetValue(message) ?? "<unknown>";

        var callId = metadata?.TryGetValue("id", out var id) == true ? id : "<none>";
        var tokens = metadata?.TryGetValue("usage", out var usage) == true ? usage : "<unknown>";

        logger.LogInformation("cursor_agent_call: step={Step}, callId={CallId}, model={Model}, tokens={Tokens}, result=<received>", step, callId, modelId, tokens);
    }

    private void LogRawCompletion(int step, string content)
    {
        var snippet = Truncate(content, 1000);
        logger.LogDebug("cursor_agent_raw: step={Step}, snippet={Snippet}", step, snippet);
    }

    private static string Truncate(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
        {
            return text;
        }

        return text[..maxLength] + $"... (+{text.Length - maxLength} chars)";
    }

    private static OpenAIPromptExecutionSettings CreateSettings()
    {
        return new OpenAIPromptExecutionSettings
        {
            Temperature = 0,
            TopP = 1,
            ResponseFormat = "json_object"
        };
    }

    private sealed record AgentCommand(string Action, IReadOnlyList<int>? Indices, string? Summary, int? FirstItemIndex)
    {
        public string? RawContent { get; init; }

        public AgentCommand WithRawContent(string raw) => this with { RawContent = raw };
    }
}
