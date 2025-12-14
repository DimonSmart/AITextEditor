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
    private const int SnapshotEvidenceLimit = 5;

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
        state = NormalizeState(state, request.TaskDescription, maxSteps);
        state = state.WithStep(new TaskLimits(state.Limits.Step, maxSteps, state.Limits.MaxSeenTail, state.Limits.MaxFound));
        sessionStore.Set(taskId, state);
        
        var completedCursors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int? firstItemIndex = null;
        string? summary = request.State?.Progress;
        CursorPortionView? lastPortion = null;
        AgentResult? agentResult = null;

        for (var step = 0; step < maxSteps; step++)
        {
            state = state.WithStep(state.Limits.NextStep());
            sessionStore.Set(taskId, state);

            if (lastPortion == null)
            {
                TryHandleCursorNext(request, completedCursors, state, out state, out lastPortion);
                sessionStore.Set(taskId, state);
            }

            var snapshotMessage = BuildSnapshotMessage(state);
            var batchMessage = lastPortion != null ? BuildBatchMessage(state, lastPortion) : "No more content.";

            var command = await GetNextCommandAsync(systemPrompt, taskMessage, snapshotMessage, batchMessage, cancellationToken, step);
            if (command == null)
            {
                logger.LogWarning("Agent response malformed.");
                continue;
            }

            (state, firstItemIndex, summary, agentResult) = ApplyCommand(state, command, firstItemIndex, summary, lastPortion, agentResult);
            sessionStore.Set(taskId, state);

            if (request.Mode == CursorAgentMode.CollectToTargetSet && command.NewEvidence?.Count > 0 && !string.IsNullOrWhiteSpace(request.TargetSetId))
            {
                var indices = MapEvidenceToIndices(command.NewEvidence, lastPortion);
                if (indices.Count > 0)
                {
                    targetSetContext.Add(request.TargetSetId!, indices);
                }
            }

            if (ShouldStop(request, state, completedCursors.Contains(request.CursorName), firstItemIndex, summary, taskId, agentResult, out var completedResult))
            {
                return completedResult;
            }

            if (command.Decision == "continue")
            {
                lastPortion = null; // Force fetch next portion
                continue;
            }
        }

        var overflowState = state.WithProgress(summary ?? state.Progress);
        sessionStore.Set(taskId, overflowState);
        return new CursorAgentResult(false, "Max steps exceeded", firstItemIndex, summary, request.TargetSetId, taskId, overflowState, 
            PointerFrom: agentResult?.SemanticPointer, 
            Excerpt: agentResult?.Excerpt ?? agentResult?.Markdown, 
            WhyThis: agentResult?.Reasons, 
            Evidence: state.Evidence,
            SemanticPointer: agentResult?.SemanticPointer, 
            Markdown: agentResult?.Markdown, 
            Confidence: agentResult?.Confidence, 
            Reasons: agentResult?.Reasons);
    }

    private static TaskState NormalizeState(TaskState state, string goal, int maxSteps)
    {
        var limits = state.Limits ?? new TaskLimits(0, maxSteps, TaskLimits.DefaultMaxSeenTail, TaskLimits.DefaultMaxFound);
        var adjustedLimits = new TaskLimits(
            Math.Min(limits.Step, maxSteps),
            maxSteps,
            limits.MaxSeenTail <= 0 ? TaskLimits.DefaultMaxSeenTail : limits.MaxSeenTail,
            limits.MaxFound <= 0 ? TaskLimits.DefaultMaxFound : limits.MaxFound);

        var seen = state.Seen ?? Array.Empty<string>();
        var evidence = state.Evidence ?? Array.Empty<EvidenceItem>();
        var progress = string.IsNullOrWhiteSpace(state.Progress) ? "not_started" : state.Progress;
        var normalizedGoal = string.IsNullOrWhiteSpace(state.Goal) ? goal : state.Goal;

        return new TaskState(normalizedGoal!, state.Found, seen, progress!, adjustedLimits, evidence);
    }

    private async Task<AgentCommand?> GetNextCommandAsync(string systemPrompt, string taskMessage, string snapshotMessage, string batchMessage, CancellationToken cancellationToken, int step)
    {
        var history = new ChatHistory();
        history.AddSystemMessage(systemPrompt);
        history.AddUserMessage(taskMessage);
        history.AddUserMessage(snapshotMessage);

        if (!string.IsNullOrWhiteSpace(batchMessage))
        {
            history.AddUserMessage(batchMessage);
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
            logger.LogDebug("cursor_agent_parsed: step={Step}, decision={Decision}, finishFound={Finish}, parsedAction={ParsedAction}", step, parsed.Decision, finishDetected, Truncate(parsedFragment ?? string.Empty, 500));
        }

        return parsed?.WithRawContent(parsedFragment ?? content);
    }

    private (TaskState State, int? FirstItemIndex, string? Summary, AgentResult? AgentResult) ApplyCommand(
        TaskState state,
        AgentCommand command,
        int? firstItemIndex,
        string? summary,
        CursorPortionView? lastPortion,
        AgentResult? agentResult)
    {
        var updated = ApplyStateUpdate(state, command.StateUpdate);

        if (command.Decision == "continue")
        {
            updated = updated with { Found = state.Found };
        }

        var result = agentResult;
        var evidenceToAdd = new List<EvidenceItem>();

        if (command.Result != null)
        {
            updated = updated with { Found = true };
            summary ??= command.Result.Excerpt ?? command.Result.Pointer;
            firstItemIndex ??= TryMapPointerToIndex(command.Result.Pointer, lastPortion);
            updated = updated.WithProgress(summary ?? updated.Progress);
            var markdown = TryFindMarkdown(command.Result.Pointer, lastPortion) ?? command.Result.Excerpt;
            var label = TryFindPointerLabel(command.Result.Pointer, lastPortion) ?? command.Result.Pointer;
            result = new AgentResult(label, markdown, command.Result.Score, command.Result.Reason, command.Result.Excerpt);
            evidenceToAdd.Add(command.Result);
        }
        else if (command.Decision == "done")
        {
            if (updated.Found != false)
            {
                updated = updated with { Found = true };
            }
            summary ??= updated.Progress;
        }
        else if (command.Decision == "not_found")
        {
            updated = updated with { Found = false };
            summary ??= command.StateUpdate?.Progress ?? updated.Progress;
            updated = updated.WithProgress(summary ?? updated.Progress);
        }

        if (command.NewEvidence?.Count > 0)
        {
            evidenceToAdd.AddRange(command.NewEvidence);
            firstItemIndex ??= TryMapPointerToIndex(command.NewEvidence[0].Pointer, lastPortion);
            if (command.Result == null)
            {
                var pointer = command.NewEvidence[0].Pointer;
                var markdown = TryFindMarkdown(pointer, lastPortion) ?? command.NewEvidence[0].Excerpt;
                var label = TryFindPointerLabel(pointer, lastPortion) ?? pointer;
                result = new AgentResult(label, markdown, command.NewEvidence[0].Score, command.NewEvidence[0].Reason, command.NewEvidence[0].Excerpt);
            }
        }

        if (evidenceToAdd.Count > 0)
        {
            updated = updated.WithEvidence(evidenceToAdd);
        }

        return (updated, firstItemIndex, summary, result);
    }

    private static TaskState ApplyStateUpdate(TaskState state, TaskStateUpdate? update)
    {
        if (update == null)
        {
            return state;
        }

        var found = update.Found ?? state.Found;
        var progress = update.Progress ?? state.Progress;

        var updated = new TaskState(state.Goal, found, state.Seen, state.Progress, state.Limits, state.Evidence);
        updated = updated.WithProgress(progress);

        return updated;
    }

    private bool ShouldStop(CursorAgentRequest request, TaskState state, bool cursorComplete, int? firstItemIndex, string? summary, string taskId, AgentResult? agentResult, out CursorAgentResult result)
    {
        if (state.Found == true)
        {
            result = BuildSuccess(request, firstItemIndex, summary ?? state.Progress, taskId, state, agentResult);
            return true;
        }

        if (state.Found == false)
        {
            result = new CursorAgentResult(false, summary ?? state.Progress, firstItemIndex, summary ?? state.Progress, request.TargetSetId, taskId, state, 
                PointerFrom: agentResult?.SemanticPointer, 
                Excerpt: agentResult?.Excerpt ?? agentResult?.Markdown, 
                WhyThis: agentResult?.Reasons, 
                Evidence: state.Evidence,
                SemanticPointer: agentResult?.SemanticPointer, 
                Markdown: agentResult?.Markdown, 
                Confidence: agentResult?.Confidence, 
                Reasons: agentResult?.Reasons);
            return true;
        }

        if (state.Limits.Remaining <= 0)
        {
            result = new CursorAgentResult(false, "TaskState limits reached", firstItemIndex, summary ?? state.Progress, request.TargetSetId, taskId, state, 
                PointerFrom: agentResult?.SemanticPointer, 
                Excerpt: agentResult?.Excerpt ?? agentResult?.Markdown, 
                WhyThis: agentResult?.Reasons, 
                Evidence: state.Evidence,
                SemanticPointer: agentResult?.SemanticPointer, 
                Markdown: agentResult?.Markdown, 
                Confidence: agentResult?.Confidence, 
                Reasons: agentResult?.Reasons);
            return true;
        }

        if (cursorComplete)
        {
            result = new CursorAgentResult(false, state.Progress ?? "Cursor exhausted with no match", firstItemIndex, summary ?? state.Progress, request.TargetSetId, taskId, state, 
                PointerFrom: agentResult?.SemanticPointer, 
                Excerpt: agentResult?.Excerpt ?? agentResult?.Markdown, 
                WhyThis: agentResult?.Reasons, 
                Evidence: state.Evidence,
                SemanticPointer: agentResult?.SemanticPointer, 
                Markdown: agentResult?.Markdown, 
                Confidence: agentResult?.Confidence, 
                Reasons: agentResult?.Reasons);
            return true;
        }

        result = default!;
        return false;
    }

    private static CursorAgentResult BuildSuccess(CursorAgentRequest request, int? firstItemIndex, string? summary, string taskId, TaskState state, AgentResult? agentResult)
    {
        return request.Mode switch
        {
            CursorAgentMode.FirstMatch => new CursorAgentResult(true, null, firstItemIndex, summary, request.TargetSetId, taskId, state, 
                PointerFrom: agentResult?.SemanticPointer, 
                Excerpt: agentResult?.Excerpt ?? agentResult?.Markdown, 
                WhyThis: agentResult?.Reasons, 
                Evidence: state.Evidence,
                SemanticPointer: agentResult?.SemanticPointer, 
                Markdown: agentResult?.Markdown, 
                Confidence: agentResult?.Confidence, 
                Reasons: agentResult?.Reasons),
            CursorAgentMode.AggregateSummary => new CursorAgentResult(true, null, null, summary, request.TargetSetId, taskId, state, 
                PointerFrom: agentResult?.SemanticPointer, 
                Excerpt: agentResult?.Excerpt ?? agentResult?.Markdown, 
                WhyThis: agentResult?.Reasons, 
                Evidence: state.Evidence,
                SemanticPointer: agentResult?.SemanticPointer, 
                Markdown: agentResult?.Markdown, 
                Confidence: agentResult?.Confidence, 
                Reasons: agentResult?.Reasons),
            CursorAgentMode.CollectToTargetSet => new CursorAgentResult(true, null, null, summary, request.TargetSetId, taskId, state, 
                PointerFrom: agentResult?.SemanticPointer, 
                Excerpt: agentResult?.Excerpt ?? agentResult?.Markdown, 
                WhyThis: agentResult?.Reasons, 
                Evidence: state.Evidence,
                SemanticPointer: agentResult?.SemanticPointer, 
                Markdown: agentResult?.Markdown, 
                Confidence: agentResult?.Confidence, 
                Reasons: agentResult?.Reasons),
            _ => new CursorAgentResult(false, "Unsupported mode", null, summary, request.TargetSetId, taskId, state, 
                PointerFrom: agentResult?.SemanticPointer, 
                Excerpt: agentResult?.Excerpt ?? agentResult?.Markdown, 
                WhyThis: agentResult?.Reasons, 
                Evidence: state.Evidence,
                SemanticPointer: agentResult?.SemanticPointer, 
                Markdown: agentResult?.Markdown, 
                Confidence: agentResult?.Confidence, 
                Reasons: agentResult?.Reasons)
        };
    }

    private bool TryHandleCursorNext(CursorAgentRequest request, HashSet<string> completedCursors, TaskState state, out TaskState updatedState, out CursorPortionView? lastPortion)
    {
        updatedState = state;
        lastPortion = null;

        if (completedCursors.Contains(request.CursorName))
        {
            return false;
        }

        var portion = documentContext.CursorContext.GetNextPortion(request.CursorName);
        if (portion == null)
        {
            return false;
        }

        var snapshot = ProjectPortion(portion);
        updatedState = UpdateSeen(state, snapshot);
        lastPortion = snapshot;
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

        return true;
    }

    private static TaskState UpdateSeen(TaskState state, CursorPortionView portion)
    {
        var seenPointers = portion.Items.Select(item => item.Pointer);
        return state.WithSeen(seenPointers);
    }

    private static string BuildBatchMessage(TaskState state, CursorPortionView portion)
    {
        var batch = new
        {
            batchId = $"cursor:{portion.CursorName}:step-{state.Limits.Step}",
            hasMore = portion.HasMore,
            items = portion.Items.Select((item, idx) => new
            {
                index = idx,
                pointerId = item.PointerId,
                pointerLabel = item.PointerLabel,
                type = item.Type,
                text = item.Text
            })
        };

        var options = new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = true
        };

        var builder = new StringBuilder();
        builder.AppendLine("Batch (JSON):");
        builder.AppendLine(JsonSerializer.Serialize(batch, options));
        return builder.ToString();
    }

    private static string BuildSnapshotMessage(TaskState state)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Task snapshot (compact):");
        builder.AppendLine($"goal: {state.Goal}");
        builder.AppendLine($"found flag: {state.Found?.ToString() ?? "undecided"}");
        builder.AppendLine($"evidenceCount: {state.Evidence.Count}");
        builder.AppendLine($"seenCount: {state.Seen.Count}");
        builder.AppendLine($"progress: {state.Progress}");
        builder.AppendLine($"limits: step={state.Limits.Step}, maxSteps={state.Limits.MaxSteps}, remaining={state.Limits.Remaining}, maxFound={state.Limits.MaxFound}");
        builder.AppendLine("dedupRule: Never return a pointer already present in alreadyFound or seenTail.");
        builder.AppendLine("stopCondition: stateUpdate.found = true|false or decision=done/not_found.");
        builder.AppendLine("first mention rule: Scan batch from top to bottom. Stop at the VERY FIRST item that matches. Do not look for 'better' matches later in the batch.");
        return builder.ToString();
    }

    private static string BuildTaskMessage(CursorAgentRequest request)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Cursor: {request.CursorName}, mode: {request.Mode}.");
        builder.AppendLine($"Goal: {request.TaskDescription}");
        builder.AppendLine("Respond with a single Decision JSON object: decision (continue|done|not_found), optional result, newEvidence array, stateUpdate, needMoreContext flag.");
        builder.AppendLine("You are processing one batch of a multi-step session. Treat Snapshot as cumulative state from previous steps.");
        builder.AppendLine("Snapshot includes goal, evidenceCount, seenCount, progress, limits.");
        builder.AppendLine("Do not treat this batch as an initial step unless Snapshot shows no prior progress; continue from the provided state.");
        builder.AppendLine("Deduplication: never return pointers already present in Snapshot.alreadyFound or Snapshot.seenTail. Use the earliest matching pointer in the batch.");
        builder.AppendLine("Stop when decision is done or not_found.");
        builder.AppendLine("Respect the first mention rule: Scan strictly from top to bottom. Prefer the first matching item in the current batch.");

        return builder.ToString();
    }

    private static string BuildSystemPrompt(CursorAgentMode mode)
    {
        var builder = new StringBuilder();
        builder.AppendLine("You are CursorAgent. Reply with a single JSON Decision object, no code fences.");
        builder.AppendLine("Decision schema: {\"decision\":\"continue|done|not_found\",\"newEvidence\":[...],\"stateUpdate\":{...},\"result\":{...},\"needMoreContext\":false}.");
        builder.AppendLine("State lives outside the model. Do not echo goal/seen/limits back.");
        builder.AppendLine("Each call is one step in a larger run: read Snapshot to understand prior findings and continue from that state without restarting.");
        builder.AppendLine("Snapshot delivers goal, evidenceCount, seenCount, progress, limits, dedupRule.");
        builder.AppendLine("Dedup rule: never repeat pointers from Snapshot.alreadyFound or Snapshot.seenTail.");
        builder.AppendLine("First mention rule: Scan the batch from top to bottom. Stop at the VERY FIRST item that matches. Do not scan the rest of the batch for 'better' matches if a valid match is found early.");
        builder.AppendLine("Type preference: When looking for content/mentions, prefer 'Paragraph' over 'Heading' unless the user asks for titles/headings.");
        builder.AppendLine("Stop-condition: set decision=done with result when goal is satisfied; use decision=not_found when exhausted.");

        if (mode == CursorAgentMode.FirstMatch)
        {
            builder.AppendLine("In FirstMatch mode, do not add items to newEvidence unless they are the match. Just output decision: continue.");
        }

        builder.AppendLine("Mode: ").Append(mode).Append('.');
        return builder.ToString();
    }

    private static CursorPortionView ProjectPortion(CursorPortion portion)
    {
        var items = portion.Items
            .Select(item => new CursorItemView(
                item.Index,
                item.Markdown,
                item.Text,
                item.Pointer.Serialize(),
                item.Pointer.Label ?? $"p{item.Index}",
                item.Type.ToString(),
                item.Pointer.Id))
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
        var finish = commands.FirstOrDefault(command => command.Decision is "done" or "not_found");
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
            var decision = root.TryGetProperty("decision", out var decisionElement) && decisionElement.ValueKind == JsonValueKind.String
                ? decisionElement.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(decision))
            {
                return null;
            }

            var stateUpdate = root.TryGetProperty("stateUpdate", out var stateElement) && stateElement.ValueKind == JsonValueKind.Object
                ? ParseStateUpdate(stateElement)
                : null;

            var newEvidence = root.TryGetProperty("newEvidence", out var evidenceElement) && evidenceElement.ValueKind == JsonValueKind.Array
                ? ParseEvidenceArray(evidenceElement)
                : null;

            var result = root.TryGetProperty("result", out var resultElement) && resultElement.ValueKind == JsonValueKind.Object
                ? ParseEvidence(resultElement)
                : null;

            var needMoreContext = root.TryGetProperty("needMoreContext", out var needElement) && needElement.ValueKind == JsonValueKind.True
                ? true
                : false;

            return new AgentCommand(decision!, newEvidence, result, needMoreContext, stateUpdate) { RawContent = content };
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

        string? progress = null;
        if (element.TryGetProperty("progress", out var progressElement) && progressElement.ValueKind != JsonValueKind.Null && progressElement.ValueKind != JsonValueKind.Undefined)
        {
            progress = progressElement.GetString();
        }

        if (progress == null && element.TryGetProperty("setContinueAfterPointer", out var continueAfter) && continueAfter.ValueKind == JsonValueKind.String)
        {
            progress = continueAfter.GetString();
        }

        return new TaskStateUpdate(goal, found, null, progress, null);
    }

    private static List<EvidenceItem> ParseEvidenceArray(JsonElement element)
    {
        var items = new List<EvidenceItem>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var evidence = ParseEvidence(item);
            if (evidence != null)
            {
                items.Add(evidence);
            }
        }

        return items;
    }

    private static EvidenceItem? ParseEvidence(JsonElement element)
    {
        string? pointer = null;
        if (element.TryGetProperty("pointerId", out var pointerIdElement))
        {
            if (pointerIdElement.ValueKind == JsonValueKind.Number)
            {
                pointer = pointerIdElement.GetInt32().ToString();
            }
            else if (pointerIdElement.ValueKind == JsonValueKind.String)
            {
                pointer = pointerIdElement.GetString();
            }
        }

        if (pointer == null && element.TryGetProperty("pointer", out var pointerElement))
        {
            if (pointerElement.ValueKind == JsonValueKind.String)
            {
                pointer = pointerElement.GetString();
            }
            else if (pointerElement.ValueKind == JsonValueKind.Number)
            {
                pointer = pointerElement.GetInt32().ToString();
            }
        }

        if (pointer == null)
        {
            return null;
        }

        var excerpt = element.TryGetProperty("excerpt", out var excerptElement) && excerptElement.ValueKind == JsonValueKind.String
            ? excerptElement.GetString()
            : null;

        if (excerpt == null && element.TryGetProperty("markdown", out var markdownElement) && markdownElement.ValueKind == JsonValueKind.String)
        {
            excerpt = markdownElement.GetString();
        }

        if (excerpt == null && element.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String)
        {
            excerpt = textElement.GetString();
        }

        var reason = element.TryGetProperty("reason", out var reasonElement) && reasonElement.ValueKind == JsonValueKind.String
            ? reasonElement.GetString()
            : null;
        double? score = null;
        if (element.TryGetProperty("score", out var scoreElement) && scoreElement.ValueKind == JsonValueKind.Number)
        {
            score = scoreElement.GetDouble();
        }

        return new EvidenceItem(pointer!, excerpt, reason, score);
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

    private static int? TryMapPointerToIndex(string pointer, CursorPortionView? portion)
    {
        if (portion == null)
        {
            return null;
        }

        if (int.TryParse(pointer, out var pointerId))
        {
            var matchById = portion.Items.FirstOrDefault(item => item.PointerId == pointerId);
            if (matchById != null)
            {
                return matchById.Index;
            }
        }

        var match = portion.Items.FirstOrDefault(item => item.Pointer.Equals(pointer, StringComparison.OrdinalIgnoreCase));
        return match?.Index;
    }

    private static string? TryFindMarkdown(string pointer, CursorPortionView? portion)
    {
        if (portion == null)
        {
            return null;
        }

        if (int.TryParse(pointer, out var pointerId))
        {
            var matchById = portion.Items.FirstOrDefault(item => item.PointerId == pointerId);
            if (matchById != null)
            {
                return matchById.Markdown;
            }
        }

        var match = portion.Items.FirstOrDefault(item => item.Pointer.Equals(pointer, StringComparison.OrdinalIgnoreCase));
        return match?.Markdown;
    }

    private static string? TryFindPointerLabel(string pointer, CursorPortionView? portion)
    {
        if (portion == null)
        {
            return null;
        }

        if (int.TryParse(pointer, out var pointerId))
        {
            var matchById = portion.Items.FirstOrDefault(item => item.PointerId == pointerId);
            if (matchById != null)
            {
                return matchById.PointerLabel;
            }
        }

        var match = portion.Items.FirstOrDefault(item => item.Pointer.Equals(pointer, StringComparison.OrdinalIgnoreCase));
        return match?.PointerLabel;
    }

    private IReadOnlyList<int> MapEvidenceToIndices(IReadOnlyList<EvidenceItem> evidence, CursorPortionView? portion)
    {
        if (portion == null)
        {
            return Array.Empty<int>();
        }

        var indices = evidence
            .Select(item => TryMapPointerToIndex(item.Pointer, portion))
            .Where(index => index.HasValue)
            .Select(index => index!.Value)
            .ToArray();

        return indices;
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

    private sealed record AgentResult(string SemanticPointer, string? Markdown, double? Confidence, string? Reasons, string? Excerpt);

    private sealed record AgentCommand(string Decision, IReadOnlyList<EvidenceItem>? NewEvidence, EvidenceItem? Result, bool NeedMoreContext, TaskStateUpdate? StateUpdate)
    {
        public string? RawContent { get; init; }

        public AgentCommand WithRawContent(string raw) => this with { RawContent = raw };
    }
}
