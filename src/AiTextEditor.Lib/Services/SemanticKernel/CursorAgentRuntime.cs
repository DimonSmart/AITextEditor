using AiTextEditor.Lib.Model;
using AiTextEditor.Lib.Services;
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
    private const int MaxSummaryLength = 500;
    private const int MaxExcerptLength = 1000;
    private const int DefaultResponseTokenLimit = 192;

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

    public async Task<CursorAgentResult> RunAsync(CursorAgentRequest request, string? targetSetId = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var requestedSteps = request.MaxSteps.GetValueOrDefault(DefaultMaxSteps);
        var maxSteps = requestedSteps > MaxStepsLimit ? MaxStepsLimit : requestedSteps;
        var agentSystemPrompt = BuildAgentSystemPrompt();
        var taskDefinitionPrompt = BuildTaskDefinitionPrompt(request);
        
        var state = request.State ?? TaskState.Create(maxSteps);
        state = NormalizeState(state, maxSteps);
        state = state.WithStep(new TaskLimits(state.Limits.Step, maxSteps, state.Limits.MaxSeenTail, state.Limits.MaxFound));
        
        var lastSeenPointer = state.Seen.LastOrDefault();
        var parameters = request.Parameters;
        if (!string.IsNullOrEmpty(lastSeenPointer))
        {
             parameters = new CursorParameters(parameters.MaxElements, parameters.MaxBytes, parameters.IncludeContent, lastSeenPointer);
        }

        var cursor = new CursorStream(documentContext.Document, parameters);
        var cursorComplete = false;
        string? summary = request.State?.Progress;
        CursorPortionView? lastPortion = null;
        AgentResult? agentResult = null;

        for (var step = 0; step < maxSteps; step++)
        {
            state = state.WithStep(state.Limits.NextStep());

            if (lastPortion == null)
            {
                TryHandleCursorNext(cursor, state, out state, out lastPortion, ref cursorComplete);
            }

            var snapshotMessage = BuildSnapshotMessage(state);
            var batchMessage = lastPortion != null ? BuildBatchMessage(state, lastPortion) : "No more content.";

            var command = await GetNextCommandAsync(agentSystemPrompt, taskDefinitionPrompt, snapshotMessage, batchMessage, cancellationToken, step);
            if (command == null)
            {
                logger.LogWarning("Agent response malformed.");
                continue;
            }

            (state, summary, agentResult) = ApplyCommand(state, command, summary, lastPortion, agentResult);

            if (command.NewEvidence?.Count > 0 && !string.IsNullOrWhiteSpace(targetSetId))
            {
                var pointers = command.NewEvidence.Select(e => e.Pointer).ToList();
                if (pointers.Count > 0)
                {
                    targetSetContext.Add(targetSetId!, pointers);
                }
            }

            if (ShouldStop(targetSetId, state, cursorComplete, summary, agentResult, out var completedResult))
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
        return new CursorAgentResult(false, "Max steps exceeded", summary, targetSetId, overflowState, 
            SemanticPointerFrom: agentResult?.SemanticPointer,
            Excerpt: Truncate(agentResult?.Excerpt ?? agentResult?.Markdown, MaxExcerptLength), 
            WhyThis: agentResult?.Reasons, 
            Evidence: state.Evidence,
            Markdown: agentResult?.Markdown, 
            Reasons: agentResult?.Reasons);
    }

    private static TaskState NormalizeState(TaskState state, int maxSteps)
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

        return new TaskState(state.Found, seen, progress!, adjustedLimits, evidence);
    }

    private async Task<AgentCommand?> GetNextCommandAsync(string agentSystemPrompt, string taskDefinitionPrompt, string snapshotMessage, string batchMessage, CancellationToken cancellationToken, int step)
    {
        var history = new ChatHistory();
        history.AddSystemMessage(agentSystemPrompt);
        history.AddUserMessage(taskDefinitionPrompt);
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

    private (TaskState State, string? Summary, AgentResult? AgentResult) ApplyCommand(
        TaskState state,
        AgentCommand command,
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

            var label = TryFindPointerLabel(command.Result.Pointer, lastPortion) ?? command.Result.Pointer;
            summary ??= $"Found match: {label}";
            summary = Truncate(summary, MaxSummaryLength);

            updated = updated.WithProgress($"Found match: {label}");

            var markdown = TryFindMarkdown(command.Result.Pointer, lastPortion) ?? command.Result.Excerpt;
            result = new AgentResult(label, markdown, command.Result.Reason, command.Result.Excerpt);
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
            summary = Truncate(summary, MaxSummaryLength);
            updated = updated.WithProgress(summary ?? updated.Progress);
        }

        if (command.NewEvidence?.Count > 0)
        {
            evidenceToAdd.AddRange(command.NewEvidence);
            if (command.Result == null)
            {
                var pointer = command.NewEvidence[0].Pointer;
                var markdown = TryFindMarkdown(pointer, lastPortion) ?? command.NewEvidence[0].Excerpt;
                var label = TryFindPointerLabel(pointer, lastPortion) ?? pointer;
                result = new AgentResult(label, markdown, command.NewEvidence[0].Reason, command.NewEvidence[0].Excerpt);
            }
        }

        if (evidenceToAdd.Count > 0)
        {
            updated = updated.WithEvidence(evidenceToAdd);
        }

        return (updated, summary, result);
    }

    private static TaskState ApplyStateUpdate(TaskState state, TaskStateUpdate? update)
    {
        if (update == null)
        {
            return state;
        }

        var found = update.Found ?? state.Found;
        var progress = update.Progress ?? state.Progress;
        progress = Truncate(progress, MaxSummaryLength);

        var updated = new TaskState(found, state.Seen, state.Progress, state.Limits, state.Evidence);
        updated = updated.WithProgress(progress);

        return updated;
    }

private bool ShouldStop(string? targetSetId, TaskState state, bool cursorComplete, string? summary, AgentResult? agentResult, out CursorAgentResult result)
    {
        if (state.Found == true)
        {
            result = BuildSuccess(targetSetId, summary ?? state.Progress, state, agentResult);
            return true;
        }

        if (state.Found == false)
        {
            result = new CursorAgentResult(false, summary ?? state.Progress, summary ?? state.Progress, targetSetId, state, 
                SemanticPointerFrom: agentResult?.SemanticPointer,
                Excerpt: Truncate(agentResult?.Excerpt ?? agentResult?.Markdown, MaxExcerptLength), 
                WhyThis: agentResult?.Reasons, 
                Evidence: state.Evidence,
                Markdown: agentResult?.Markdown, 
                Reasons: agentResult?.Reasons);
            return true;
        }

        if (state.Limits.Remaining <= 0)
        {
            result = new CursorAgentResult(false, "TaskState limits reached", summary ?? state.Progress, targetSetId, state, 
                SemanticPointerFrom: agentResult?.SemanticPointer,
                Excerpt: Truncate(agentResult?.Excerpt ?? agentResult?.Markdown, MaxExcerptLength), 
                WhyThis: agentResult?.Reasons, 
                Evidence: state.Evidence,
                Markdown: agentResult?.Markdown, 
                Reasons: agentResult?.Reasons);
            return true;
        }

        if (cursorComplete)
        {
            result = new CursorAgentResult(false, state.Progress ?? "Cursor exhausted with no match", summary ?? state.Progress, targetSetId, state, 
                SemanticPointerFrom: agentResult?.SemanticPointer,
                Excerpt: agentResult?.Excerpt ?? agentResult?.Markdown, 
                WhyThis: agentResult?.Reasons, 
                Evidence: state.Evidence,
                Markdown: agentResult?.Markdown, 
                Reasons: agentResult?.Reasons);
            return true;
        }

        result = default!;
        return false;
    }

    private static CursorAgentResult BuildSuccess(string? targetSetId, string? summary, TaskState state, AgentResult? agentResult)
    {
        return new CursorAgentResult(true, null, summary, targetSetId, state,
            SemanticPointerFrom: agentResult?.SemanticPointer,
            Excerpt: Truncate(agentResult?.Excerpt ?? agentResult?.Markdown, MaxExcerptLength),
            WhyThis: agentResult?.Reasons,
            Evidence: state.Evidence,
            Markdown: agentResult?.Markdown,
            Reasons: agentResult?.Reasons);
    }

    private bool TryHandleCursorNext(CursorStream cursor, TaskState state, out TaskState updatedState, out CursorPortionView? lastPortion, ref bool cursorComplete)
    {
        updatedState = state;
        lastPortion = null;

        if (cursorComplete)
        {
            return false;
        }

        var portion = cursor.NextPortion();
        if (portion == null)
        {
            cursorComplete = true;
            return false;
        }

        var snapshot = ProjectPortion(portion);
        updatedState = UpdateSeen(state, snapshot);
        lastPortion = snapshot;
        var pointerLabel = snapshot.Items.FirstOrDefault()?.Pointer ?? "<none>";
        var snippet = snapshot.HasMore && snapshot.Items.Count > 0 ? Truncate(snapshot.Items[0].Markdown, 200) : string.Empty;
        var eventName = snapshot.HasMore ? "cursor_batch" : "cursor_batch_complete";
        logger.LogDebug("{Event}: count={Count}, hasMore={HasMore}, pointerLabel={PointerLabel}, snippet={Snippet}", eventName, snapshot.Items.Count, snapshot.HasMore, pointerLabel, snippet);

        if (!snapshot.HasMore)
        {
            cursorComplete = true;
            if (updatedState.Found != true)
            {
                updatedState = updatedState.WithProgress("Cursor stream is complete.");
            }
        }

        return true;
    }

    private static TaskState UpdateSeen(TaskState state, CursorPortionView snapshot)
    {
        var merged = new List<string>(state.Seen);
        foreach (var item in snapshot.Items)
        {
            if (!merged.Any(x => x.Equals(item.Pointer, StringComparison.OrdinalIgnoreCase)))
            {
                merged.Add(item.Pointer);
            }
        }

        if (merged.Count > state.Limits.MaxSeenTail)
        {
            merged.RemoveRange(0, merged.Count - state.Limits.MaxSeenTail);
        }

        return state with { Seen = merged };
    }

    private static string BuildBatchMessage(TaskState state, CursorPortionView portion)
    {
        var batch = new
        {
            batchId = $"cursor:local:step-{state.Limits.Step}",
            firstBatch = state.Limits.Step == 1,
            lastBatch = !portion.HasMore,
            hasMore = portion.HasMore,
            items = portion.Items.Select((item, idx) => new
            {
                index = idx,
                pointer = item.Pointer,
                type = item.Type,
                markdown = item.Markdown
            })
        };

        var options = new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = false
        };

        return JsonSerializer.Serialize(batch, options);
    }

    private static string BuildSnapshotMessage(TaskState state)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Task snapshot:");
        builder.AppendLine($"counts: evidence={state.Evidence.Count}, seen={state.Seen.Count}");
        builder.AppendLine($"progress: {state.Progress}");
        builder.AppendLine($"limits: step={state.Limits.Step}, maxSteps={state.Limits.MaxSteps}, remaining={state.Limits.Remaining}, maxFound={state.Limits.MaxFound}");
        builder.AppendLine("dedup: skip pointers from alreadyFound or seenTail.");
        builder.AppendLine("first mention: stop at the first valid match in the batch.");
        return builder.ToString();
    }

    private static string BuildTaskDefinitionPrompt(CursorAgentRequest request)
    {
        var b = new StringBuilder();
        b.AppendLine($"Cursor params: maxElements={request.Parameters.MaxElements}, maxBytes={request.Parameters.MaxBytes}, includeContent={request.Parameters.IncludeContent}.");
        b.AppendLine($"Goal: {request.TaskDescription}");
        b.AppendLine("");
        b.AppendLine("Input you receive each step:");
        b.AppendLine("- Task snapshot (counts, progress, limits).");
        b.AppendLine("- Batch JSON with: hasMore, firstBatch, lastBatch, items[]. Each item has index, pointer, type, markdown.");
        b.AppendLine("");
        b.AppendLine("Your job for THIS step:");
        b.AppendLine("1) Read Batch JSON items in order index=0..N.");
        b.AppendLine("2) If an item satisfies the Goal, immediately return decision=\"done\" with result:");
        b.AppendLine("   - result.pointer = that item.pointer");
        b.AppendLine("   - result.excerpt = short direct quote from item.markdown that contains the match (120..300 chars)");
        b.AppendLine("   - result.reason = 1 short sentence why it matches");
        b.AppendLine("3) If no item matches:");
        b.AppendLine("   - if hasMore=true => decision=\"continue\"");
        b.AppendLine("   - else => decision=\"not_found\"");
        b.AppendLine("");
        b.AppendLine("Evidence:");
        b.AppendLine("- newEvidence is optional. If you add it, use the same excerpt rule and keep it small (0..3 items).");
        b.AppendLine("");
        b.AppendLine("Do not output anything except the single JSON object.");
        return b.ToString();
    }


    private static string BuildAgentSystemPrompt()
    {
        var b = new StringBuilder();
        b.AppendLine("You are CursorAgent.");
        b.AppendLine("Reply with exactly ONE JSON object. No code fences. No extra text.");
        b.AppendLine("");
        b.AppendLine("Schema:");
        b.AppendLine("{");
        b.AppendLine("  \"decision\": \"continue|done|not_found\",");
        b.AppendLine("  \"result\": {\"pointer\": \"...\", \"excerpt\": \"...\", \"reason\": \"...\"} (only when decision=done),");
        b.AppendLine("  \"newEvidence\": [{\"pointer\":\"...\",\"excerpt\":\"...\",\"reason\":\"...\"}],");
        b.AppendLine("  \"stateUpdate\": {\"found\": true|false, \"progress\": \"...\"}");
        b.AppendLine("}");
        b.AppendLine("");
        b.AppendLine("Rules:");
        b.AppendLine("- If you found a match: decision MUST be \"done\" AND result MUST be present.");
        b.AppendLine("- If decision is \"done\" but result is missing: this is INVALID. Never do that.");
        b.AppendLine("- If no match in this batch and hasMore=true: decision=\"continue\".");
        b.AppendLine("- If no match and hasMore=false (last batch): decision=\"not_found\".");
        b.AppendLine("- Scan items strictly top-to-bottom by index. Stop at the FIRST valid match.");
        b.AppendLine("- Prefer type=\"Paragraph\" over \"Heading\" unless the goal explicitly wants headings/titles.");
        b.AppendLine("");
        b.AppendLine("Excerpt rule (proof):");
        b.AppendLine("- excerpt MUST be a short direct quote copied from item.markdown.");
        b.AppendLine("- excerpt MUST include the matched word/name.");
        b.AppendLine("- Keep excerpt 120..300 characters. Do NOT paraphrase.");
        b.AppendLine("");
        b.AppendLine("stateUpdate.progress:");
        b.AppendLine("- Very short: e.g. \"scanned_batch\", \"found_match\", \"no_match_continue\", \"no_match_last_batch\".");
        b.AppendLine("- Do NOT write a narrative log.");
        b.AppendLine("");
        b.AppendLine("Keep the whole response short.");
        return b.ToString();
    }


    private static CursorPortionView ProjectPortion(CursorPortion portion) => CursorPortionView.FromPortion(portion);

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

        return new TaskStateUpdate(found, null, progress, null);
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
        if (element.TryGetProperty("pointer", out var pointerElement) && pointerElement.ValueKind == JsonValueKind.String)
        {
            pointer = pointerElement.GetString();
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

        return new EvidenceItem(pointer!, excerpt, reason);
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

    private static string? TryFindMarkdown(string pointer, CursorPortionView? portion)
    {
        if (portion == null)
        {
            return null;
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

        var match = portion.Items.FirstOrDefault(item => item.Pointer.Equals(pointer, StringComparison.OrdinalIgnoreCase));
        return match?.Pointer;
    }

    private void LogCompletionSkeleton(int step, object? message)
    {
        if (message == null)
        {
            logger.LogInformation("cursor_agent_call: step={Step}, model=<unknown>, tokens=<unknown>, result=<empty>", step);
            return;
        }

        var metadata = message.GetType().GetProperty("Metadata")?.GetValue(message) as IReadOnlyDictionary<string, object?>;
        var modelId = message.GetType().GetProperty("ModelId")?.GetValue(message) ?? "<unknown>";

        var tokens = metadata?.TryGetValue("usage", out var usage) == true ? usage : "<unknown>";

        logger.LogInformation("cursor_agent_call: step={Step}, model={Model}, tokens={Tokens}, result=<received>", step, modelId, tokens);
    }

    private void LogRawCompletion(int step, string content)
    {
        var snippet = Truncate(content, 1000);
        logger.LogDebug("cursor_agent_raw: step={Step}, snippet={Snippet}", step, snippet);
    }

    private static string? Truncate(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
        {
            return text;
        }

        return text[..maxLength] + $"... (+{text.Length - maxLength} chars)";
    }

    private OpenAIPromptExecutionSettings CreateSettings()
    {
        return new OpenAIPromptExecutionSettings
        {
            Temperature = 0,
            TopP = 1,
            ResponseFormat = "json_object",
            MaxTokens = DefaultResponseTokenLimit,
            ExtensionData = new Dictionary<string, object>
            {
                ["options"] = new Dictionary<string, object>
                {
                    ["think"] = false,
                    ["num_predict"] = DefaultResponseTokenLimit,
                }
            }
        };
    }

    private sealed record AgentResult(string SemanticPointer, string? Markdown, string? Reasons, string? Excerpt);

    private sealed record AgentCommand(string Decision, IReadOnlyList<EvidenceItem>? NewEvidence, EvidenceItem? Result, bool NeedMoreContext, TaskStateUpdate? StateUpdate)
    {
        public string? RawContent { get; init; }

        public AgentCommand WithRawContent(string raw) => this with { RawContent = raw };
    }
}
