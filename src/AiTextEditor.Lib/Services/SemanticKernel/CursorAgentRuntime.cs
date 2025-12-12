using AiTextEditor.Lib.Model;
using DimonSmart.AiUtils;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace AiTextEditor.Lib.Services.SemanticKernel;

public sealed class CursorAgentRuntime
{
    internal const int DefaultMaxSteps = 128;
    internal const int MaxStepsLimit = 512;

    private readonly DocumentContext documentContext;
    private readonly TargetSetContext targetSetContext;
    private readonly SessionStore sessionStore;
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
        sessionStore = documentContext.SessionStore ?? throw new ArgumentNullException(nameof(documentContext.SessionStore));
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
        var taskId = string.IsNullOrWhiteSpace(request.TaskId) ? Guid.NewGuid().ToString("N") : request.TaskId!;
        var state = sessionStore.GetOrAdd(taskId, () => request.State ?? TaskState.Create(request.TaskDescription, maxSteps));
        state = state.WithStep(new TaskLimits(state.Limits.Step, maxSteps));
        sessionStore.Set(taskId, state);
        string? lastPortionMessage = null;
        var completedCursors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int? firstItemIndex = null;
        string? summary = request.State?.Progress;

        for (var step = 0; step < maxSteps; step++)
        {
            state = state.WithStep(state.Limits.NextStep());
            sessionStore.Set(taskId, state);

            var snapshotMessage = BuildSnapshotMessage(state);
            var command = await GetNextCommandAsync(systemPrompt, taskMessage, snapshotMessage, lastPortionMessage, cancellationToken, step);
            if (command == null)
            {
                lastPortionMessage = "Agent response malformed. Respond with JSON action and stateUpdate.";
                continue;
            }

            (state, firstItemIndex, summary) = ApplyCommand(state, command, firstItemIndex, summary);
            sessionStore.Set(taskId, state);

            if (ShouldStop(request, state, completedCursors.Contains(request.CursorName), firstItemIndex, summary, taskId, out var completedResult))
            {
                return completedResult;
            }

            if (TryHandleAction(request, command, completedCursors, state, out state, out var newPortionMessage))
            {
                sessionStore.Set(taskId, state);
                lastPortionMessage = newPortionMessage;

                if (ShouldStop(request, state, completedCursors.Contains(request.CursorName), firstItemIndex, summary, taskId, out completedResult))
                {
                    return completedResult;
                }

                continue;
            }

            lastPortionMessage = "Unknown action. Use cursor_next, target_set_add, agent_finish_success, agent_finish_not_found with stateUpdate.";
        }

        var overflowState = state.WithProgress(summary ?? state.Progress);
        sessionStore.Set(taskId, overflowState);
        return new CursorAgentResult(false, "Max steps exceeded", firstItemIndex, summary, request.TargetSetId, taskId, overflowState);
    }

    private async Task<AgentCommand?> GetNextCommandAsync(string systemPrompt, string taskMessage, string snapshotMessage, string? lastPortionMessage, CancellationToken cancellationToken, int step)
    {
        var history = new ChatHistory();
        history.AddSystemMessage(systemPrompt);
        history.AddUserMessage(taskMessage);
        history.AddUserMessage(snapshotMessage);

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

    private (TaskState State, int? FirstItemIndex, string? Summary) ApplyCommand(TaskState state, AgentCommand command, int? firstItemIndex, string? summary)
    {
        var updated = ApplyStateUpdate(state, command.StateUpdate);

        if (IsFinishSuccess(command.Action))
        {
            updated = updated with { Found = true };
            summary ??= command.Summary;
            firstItemIndex ??= command.FirstItemIndex;
            updated = updated.WithProgress(command.Summary ?? updated.Progress);
        }
        else if (IsFinishNotFound(command.Action))
        {
            updated = updated with { Found = false };
            summary ??= command.Summary ?? "not_found";
            updated = updated.WithProgress(command.Summary ?? "not_found");
        }
        else if (command.StateUpdate?.Found == true)
        {
            summary ??= command.Summary ?? updated.Progress;
        }
        else if (command.StateUpdate?.Found == false)
        {
            summary ??= command.Summary ?? updated.Progress;
            updated = updated.WithProgress(command.Summary ?? updated.Progress);
        }

        return (updated, firstItemIndex, summary);
    }

    private static TaskState ApplyStateUpdate(TaskState state, TaskStateUpdate? update)
    {
        if (update == null)
        {
            return state;
        }

        var goal = update.Goal ?? state.Goal;
        var found = update.Found ?? state.Found;
        var seen = update.Seen != null ? MergeSeen(state.Seen, update.Seen) : state.Seen;
        var progress = update.Progress ?? state.Progress;
        var limits = update.Limits ?? state.Limits;

        return new TaskState(goal, found, seen, progress, limits);
    }

    private bool ShouldStop(CursorAgentRequest request, TaskState state, bool cursorComplete, int? firstItemIndex, string? summary, string taskId, out CursorAgentResult result)
    {
        if (state.Found == true)
        {
            result = BuildSuccess(request, firstItemIndex, summary ?? state.Progress, taskId, state);
            return true;
        }

        if (state.Found == false)
        {
            result = new CursorAgentResult(false, summary ?? state.Progress, firstItemIndex, summary ?? state.Progress, request.TargetSetId, taskId, state);
            return true;
        }

        if (state.Limits.Remaining <= 0)
        {
            result = new CursorAgentResult(false, "TaskState limits reached", firstItemIndex, summary ?? state.Progress, request.TargetSetId, taskId, state);
            return true;
        }

        if (cursorComplete)
        {
            result = new CursorAgentResult(false, state.Progress ?? "Cursor exhausted with no match", firstItemIndex, summary ?? state.Progress, request.TargetSetId, taskId, state);
            return true;
        }

        result = default!;
        return false;
    }

    private static CursorAgentResult BuildSuccess(CursorAgentRequest request, int? firstItemIndex, string? summary, string taskId, TaskState state)
    {
        return request.Mode switch
        {
            CursorAgentMode.FirstMatch => new CursorAgentResult(true, null, firstItemIndex, summary, request.TargetSetId, taskId, state),
            CursorAgentMode.AggregateSummary => new CursorAgentResult(true, null, null, summary, request.TargetSetId, taskId, state),
            CursorAgentMode.CollectToTargetSet => new CursorAgentResult(true, null, null, summary, request.TargetSetId, taskId, state),
            _ => new CursorAgentResult(false, "Unsupported mode", null, summary, request.TargetSetId, taskId, state)
        };
    }

    private bool TryHandleAction(CursorAgentRequest request, AgentCommand command, HashSet<string> completedCursors, TaskState state, out TaskState updatedState, out string? portionMessage)
    {
        portionMessage = null;
        updatedState = state;

        if (IsCursorNext(command.Action))
        {
            return TryHandleCursorNext(request, completedCursors, state, out updatedState, out portionMessage);
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

    private static bool IsFinishSuccess(string action) => action.Equals("agent_finish_success", StringComparison.OrdinalIgnoreCase);

    private static bool IsFinishNotFound(string action) => action.Equals("agent_finish_not_found", StringComparison.OrdinalIgnoreCase);

    private bool TryHandleCursorNext(CursorAgentRequest request, HashSet<string> completedCursors, TaskState state, out TaskState updatedState, out string? portionMessage)
    {
        updatedState = state;
        portionMessage = null;

        if (completedCursors.Contains(request.CursorName))
        {
            portionMessage = $"cursor_next halted: cursor '{request.CursorName}' is complete. Use agent_finish_* or stateUpdate.found.";
            return true;
        }

        var portion = documentContext.CursorContext.GetNextPortion(request.CursorName);
        if (portion == null)
        {
            portionMessage = "cursor_next failed: cursor not found";
            return true;
        }

        var snapshot = ProjectPortion(portion);
        updatedState = UpdateSeen(state, snapshot);
        var pointerLabel = snapshot.Items.FirstOrDefault()?.PointerLabel ?? "<none>";
        var snippet = snapshot.HasMore && snapshot.Items.Count > 0 ? Truncate(snapshot.Items[0].Markdown, 200) : string.Empty;
        var eventName = snapshot.HasMore ? "cursor_batch" : "cursor_batch_complete";
        logger.LogDebug("{Event}: cursor={Cursor}, count={Count}, hasMore={HasMore}, pointerLabel={PointerLabel}, snippet={Snippet}", eventName, snapshot.CursorName, snapshot.Items.Count, snapshot.HasMore, pointerLabel, snippet);

        if (!snapshot.HasMore)
        {
            completedCursors.Add(snapshot.CursorName);
            if (updatedState.Found != true)
            {
                updatedState = updatedState.WithProgress("Cursor stream is complete.");
            }
        }

        portionMessage = BuildPortionFeedback(snapshot);
        return true;
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

    private static TaskState UpdateSeen(TaskState state, CursorPortionView portion)
    {
        var seenPointers = portion.Items.Select(item => item.Pointer);
        return state.WithSeen(seenPointers);
    }

    private static IReadOnlyCollection<string> MergeSeen(IReadOnlyCollection<string> existing, IReadOnlyCollection<string> incoming)
    {
        var merged = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        foreach (var item in incoming)
        {
            merged.Add(item);
        }

        return merged.ToArray();
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

        builder.AppendLine("Return only one JSON action with optional stateUpdate.");
        return builder.ToString();
    }

    private static string BuildSnapshotMessage(TaskState state)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Task snapshot:");
        builder.AppendLine($"goal: {state.Goal}");
        builder.AppendLine($"found: {state.Found?.ToString() ?? "undecided"}");
        builder.AppendLine($"seenCount: {state.Seen.Count}");
        builder.AppendLine($"progress: {state.Progress}");
        builder.AppendLine($"limits: step={state.Limits.Step}, maxSteps={state.Limits.MaxSteps}, remaining={state.Limits.Remaining}");
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
        builder.AppendLine("Always include stateUpdate to reflect the new task state: goal, found, seen, progress, limits(step,maxSteps).");
        builder.AppendLine("Choose the earliest matching item in the batch. Include pointerLabel and pointer in summaries.");
        builder.AppendLine("Stop after the first relevant paragraph. If text does not contain the normalized term, keep requesting cursor_next.");
        builder.AppendLine("Mode: ").Append(mode).Append(". Return format: {\"action\":...,\"indices\":[...],\"firstItemIndex\":n,\"summary\":\"text\",\"stateUpdate\":{...}}.");
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

            var stateUpdate = root.TryGetProperty("stateUpdate", out var stateElement) && stateElement.ValueKind == JsonValueKind.Object
                ? ParseStateUpdate(stateElement)
                : null;

            return new AgentCommand(action!, indices, summary, firstIndex, stateUpdate) { RawContent = content };
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static TaskStateUpdate ParseStateUpdate(JsonElement element)
    {
        string? goal = null;
        if (element.TryGetProperty("goal", out var goalElement) && goalElement.ValueKind == JsonValueKind.String)
        {
            goal = goalElement.GetString();
        }

        bool? found = null;
        if (element.TryGetProperty("found", out var foundElement) && foundElement.ValueKind == JsonValueKind.True)
        {
            found = true;
        }
        else if (element.TryGetProperty("found", out foundElement) && foundElement.ValueKind == JsonValueKind.False)
        {
            found = false;
        }

        IReadOnlyCollection<string>? seen = null;
        if (element.TryGetProperty("seen", out var seenElement) && seenElement.ValueKind == JsonValueKind.Array)
        {
            seen = seenElement.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString()!)
                .ToArray();
        }

        string? progress = null;
        if (element.TryGetProperty("progress", out var progressElement) && progressElement.ValueKind != JsonValueKind.Null && progressElement.ValueKind != JsonValueKind.Undefined)
        {
            progress = progressElement.GetString();
        }

        TaskLimits? limits = null;
        if (element.TryGetProperty("limits", out var limitsElement) && limitsElement.ValueKind == JsonValueKind.Object)
        {
            var step = limitsElement.TryGetProperty("step", out var stepElement) && stepElement.ValueKind == JsonValueKind.Number
                ? stepElement.GetInt32()
                : (int?)null;
            var maxSteps = limitsElement.TryGetProperty("maxSteps", out var maxElement) && maxElement.ValueKind == JsonValueKind.Number
                ? maxElement.GetInt32()
                : (int?)null;

            if (step.HasValue && maxSteps.HasValue)
            {
                limits = new TaskLimits(step.Value, maxSteps.Value);
            }
        }

        return new TaskStateUpdate(goal, found, seen, progress, limits);
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

    private sealed record AgentCommand(string Action, IReadOnlyList<int>? Indices, string? Summary, int? FirstItemIndex, TaskStateUpdate? StateUpdate)
    {
        public string? RawContent { get; init; }

        public AgentCommand WithRawContent(string raw) => this with { RawContent = raw };
    }
}
