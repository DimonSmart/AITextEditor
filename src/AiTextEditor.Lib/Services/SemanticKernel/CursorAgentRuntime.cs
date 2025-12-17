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
    internal const int DefaultMaxFound = 20;
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

        var state = request.State ?? new CursorAgentState(Array.Empty<EvidenceItem>());
        state = state.WithEvidence(Array.Empty<EvidenceItem>(), DefaultMaxFound);
        var afterPointer = request.StartAfterPointer;
        var cursorComplete = false;
        string? summary = null;
        CursorPortionView? lastPortion = null;
        string? stopReason = null;
        var stepsUsed = 0;

        var parameters = new CursorParameters(request.Parameters.MaxElements, request.Parameters.MaxBytes, request.Parameters.IncludeContent, afterPointer);
        var cursor = new CursorStream(documentContext.Document, parameters);

        for (var step = 0; step < maxSteps; step++)
        {
            if (lastPortion == null)
            {
                if (!TryHandleCursorNext(cursor, out lastPortion, ref afterPointer, ref cursorComplete))
                {
                    if (cursorComplete)
                    {
                        stopReason = "cursor_complete";
                        break;
                    }

                    continue;
                }
            }

            var snapshotMessage = BuildSnapshotMessage(state, step, afterPointer);
            var batchMessage = lastPortion != null ? BuildBatchMessage(lastPortion, step) : "No more content.";

            var command = await GetNextCommandAsync(agentSystemPrompt, taskDefinitionPrompt, snapshotMessage, batchMessage, cancellationToken, step);
            stepsUsed = step + 1;

            if (command == null)
            {
                logger.LogWarning("Agent response malformed.");
                if (cursorComplete)
                {
                    stopReason = "cursor_complete";
                    break;
                }

                lastPortion = null;
                continue;
            }

            (state, summary) = ApplyCommand(state, command, summary, lastPortion);

            if (!string.IsNullOrWhiteSpace(targetSetId) && command.NewEvidence?.Count > 0)
            {
                var pointers = command.NewEvidence.Select(e => e.Pointer).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (pointers.Count > 0)
                {
                    targetSetContext.Add(targetSetId!, pointers);
                }
            }

            if (ShouldStop(command.Decision, cursorComplete, stepsUsed, maxSteps, out stopReason))
            {
                break;
            }

            if (command.Decision == "continue")
            {
                lastPortion = null;
                continue;
            }

            lastPortion = null;
        }

        stopReason ??= "max_steps";
        var finalCursorComplete = cursorComplete || string.Equals(stopReason, "cursor_complete", StringComparison.OrdinalIgnoreCase);
        return await BuildResultByFinalizerAsync(request.TaskDescription, state, summary, targetSetId, stopReason, afterPointer, finalCursorComplete, stepsUsed, cancellationToken);
    }

    private async Task<CursorAgentResult> BuildResultByFinalizerAsync(
        string taskDescription,
        CursorAgentState state,
        string? summary,
        string? targetSetId,
        string stopReason,
        string? nextAfterPointer,
        bool cursorComplete,
        int stepsUsed,
        CancellationToken cancellationToken)
    {
        if (state.Evidence.Count == 0)
        {
            return new CursorAgentResult(
                false,
                stopReason,
                summary,
                targetSetId,
                state,
                Evidence: state.Evidence,
                NextAfterPointer: nextAfterPointer,
                CursorComplete: cursorComplete);
        }

        var history = new ChatHistory();
        history.AddSystemMessage(BuildFinalizerSystemPrompt());

        var evidenceJson = SerializeEvidence(state.Evidence);
        history.AddUserMessage(BuildFinalizerUserMessage(taskDescription, evidenceJson, cursorComplete, stepsUsed, nextAfterPointer));

        var response = await chatService.GetChatMessageContentsAsync(history, CreateSettings(), cancellationToken: cancellationToken);
        var message = response.FirstOrDefault();
        var content = message?.Content ?? string.Empty;
        LogCompletionSkeleton(stepsUsed, message);
        LogRawCompletion(stepsUsed, content);

        var parsed = ParseFinalizer(content);
        if (parsed == null || parsed.Decision == "not_found")
        {
            return new CursorAgentResult(
                false,
                stopReason,
                summary,
                targetSetId,
                state,
                Evidence: state.Evidence,
                NextAfterPointer: nextAfterPointer,
                CursorComplete: cursorComplete);
        }

        if (parsed.Decision != "success" || string.IsNullOrWhiteSpace(parsed.SemanticPointerFrom) || !state.Evidence.Any(e => e.Pointer.Equals(parsed.SemanticPointerFrom, StringComparison.OrdinalIgnoreCase)))
        {
            logger.LogWarning("finalizer_pointer_missing_or_invalid");
            return new CursorAgentResult(
                false,
                "finalizer_missing_pointer",
                summary,
                targetSetId,
                state,
                Evidence: state.Evidence,
                NextAfterPointer: nextAfterPointer,
                CursorComplete: cursorComplete);
        }

        var finalSummary = string.IsNullOrWhiteSpace(parsed.Summary) ? summary : Truncate(parsed.Summary, MaxSummaryLength);

        return new CursorAgentResult(
            true,
            null,
            finalSummary,
            targetSetId,
            state,
            SemanticPointerFrom: parsed.SemanticPointerFrom,
            Excerpt: Truncate(parsed.Excerpt, MaxExcerptLength),
            WhyThis: parsed.WhyThis,
            Evidence: state.Evidence,
            NextAfterPointer: nextAfterPointer,
            CursorComplete: cursorComplete,
            Markdown: parsed.Markdown,
            Reasons: parsed.WhyThis);
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

    private (CursorAgentState State, string? Summary) ApplyCommand(
        CursorAgentState state,
        AgentCommand command,
        string? summary,
        CursorPortionView? lastPortion)
    {
        var updatedSummary = string.IsNullOrWhiteSpace(command.Progress) ? summary : Truncate(command.Progress, MaxSummaryLength);
        var evidenceToAdd = NormalizeEvidence(command.NewEvidence ?? Array.Empty<EvidenceItem>(), lastPortion);
        var updated = evidenceToAdd.Count > 0 ? state.WithEvidence(evidenceToAdd, DefaultMaxFound) : state;

        return (updated, updatedSummary ?? summary);
    }

    private static bool ShouldStop(string decision, bool cursorComplete, int step, int maxSteps, out string reason)
    {
        if (decision is "done" or "not_found")
        {
            reason = $"decision_{decision}";
            return true;
        }

        if (cursorComplete)
        {
            reason = "cursor_complete";
            return true;
        }

        if (step >= maxSteps)
        {
            reason = "max_steps";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    private bool TryHandleCursorNext(CursorStream cursor, out CursorPortionView? lastPortion, ref string? afterPointer, ref bool cursorComplete)
    {
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
        lastPortion = snapshot;
        var pointerLabel = snapshot.Items.FirstOrDefault()?.Pointer ?? "<none>";
        var snippet = snapshot.HasMore && snapshot.Items.Count > 0 ? Truncate(snapshot.Items[0].Markdown, 200) : string.Empty;
        var eventName = snapshot.HasMore ? "cursor_batch" : "cursor_batch_complete";
        logger.LogDebug("{Event}: count={Count}, hasMore={HasMore}, pointerLabel={PointerLabel}, snippet={Snippet}", eventName, snapshot.Items.Count, snapshot.HasMore, pointerLabel, snippet);

        if (snapshot.Items.Count > 0)
        {
            afterPointer = snapshot.Items[^1].Pointer;
        }
        else
        {
            cursorComplete = true;
        }

        if (!snapshot.HasMore)
        {
            cursorComplete = true;
        }

        return true;
    }

    private static string BuildBatchMessage(CursorPortionView portion, int step)
    {
        var batch = new
        {
            firstBatch = step == 0,
            lastBatch = !portion.HasMore,
            hasMore = portion.HasMore,
            items = portion.Items.Select((item, itemIndex) => new
            {
                batchItemIndex = itemIndex,
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

    private static string BuildSnapshotMessage(CursorAgentState state, int step, string? afterPointer)
    {
        var recentPointers = state.Evidence
            .Skip(Math.Max(0, state.Evidence.Count - SnapshotEvidenceLimit))
            .Select(e => e.Pointer);

        var builder = new StringBuilder();
        builder.AppendLine("Cursor snapshot:");
        builder.AppendLine($"step: {step}");
        builder.AppendLine($"evidenceCount: {state.Evidence.Count}");
        builder.AppendLine($"afterPointer: {afterPointer ?? "<none>"}");

        var pointerList = string.Join(", ", recentPointers);
        if (!string.IsNullOrWhiteSpace(pointerList))
        {
            builder.AppendLine($"recentEvidencePointers: {pointerList}");
        }

        return builder.ToString();
    }

    private static string BuildTaskDefinitionPrompt(CursorAgentRequest request)
    {
        var b = new StringBuilder();
        b.AppendLine($"Cursor params: maxElements={request.Parameters.MaxElements}, maxBytes={request.Parameters.MaxBytes}, includeContent={request.Parameters.IncludeContent}.");
        b.AppendLine($"Goal: {request.TaskDescription}");
        b.AppendLine("");
        b.AppendLine("Input you receive each step:");
        b.AppendLine("- Task snapshot (evidenceCount, afterPointer, step, recent evidence pointers).");
        b.AppendLine("- Batch JSON with: hasMore, firstBatch, lastBatch, items[]. Each item has batchItemIndex, pointer, type, markdown.");
        b.AppendLine("");
        b.AppendLine("Your job for THIS step:");
        b.AppendLine("1) Read Batch JSON items in order batchItemIndex=0..N.");
        b.AppendLine("2) If an item satisfies the Goal, add it to newEvidence (max 3 per step).");
        b.AppendLine("3) decision rules:");
        b.AppendLine("   - decision=\"continue\" when you need more batches.");
        b.AppendLine("   - decision=\"done\" only when you believe the goal is satisfied based on evidence collected so far.");
        b.AppendLine("   - decision=\"not_found\" only when this is the last batch (hasMore=false or cursorComplete=true) and no matches exist in any step.");
        b.AppendLine("");
        b.AppendLine("Evidence:");
        b.AppendLine("- newEvidence contains only candidates from THIS step (0..3 items).");
        b.AppendLine("- Do not pretend this is the final answer; another model will finalize after the scan phase.");
        b.AppendLine("");
        b.AppendLine("progress (optional): very short status for logging.");
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
        b.AppendLine("  \"newEvidence\": [{\"pointer\":\"...\",\"excerpt\":\"...\",\"reason\":\"...\"}],");
        b.AppendLine("  \"progress\": \"...\" // optional short log");
        b.AppendLine("}");
        b.AppendLine("");
        b.AppendLine("Rules:");
        b.AppendLine("- Add up to 3 items to newEvidence when they look relevant.");
        b.AppendLine("- decision=\"done\" when the goal is satisfied with collected evidence.");
        b.AppendLine("- decision=\"continue\" when you need more context.");
        b.AppendLine("- decision=\"not_found\" only if this is the end (hasMore=false or cursorComplete=true) and no matches exist.");
        b.AppendLine("- Scan items strictly top-to-bottom by batchItemIndex.");
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

    private static string BuildFinalizerSystemPrompt()
    {
        var b = new StringBuilder();
        b.AppendLine("You finalize the cursor scan results.");
        b.AppendLine("Respond with exactly ONE JSON object. No code fences. No extra text.");
        b.AppendLine("");
        b.AppendLine("Schema:");
        b.AppendLine("{");
        b.AppendLine("  \"decision\": \"success|not_found\",");
        b.AppendLine("  \"semanticPointerFrom\": \"...\", // must come from evidence when success");
        b.AppendLine("  \"excerpt\": \"...\",");
        b.AppendLine("  \"whyThis\": \"...\",");
        b.AppendLine("  \"markdown\": \"...\",");
        b.AppendLine("  \"summary\": \"...\"");
        b.AppendLine("}");
        b.AppendLine("");
        b.AppendLine("Rules:");
        b.AppendLine("- Use provided evidence only; do not invent new pointers.");
        b.AppendLine("- semanticPointerFrom MUST be one of the evidence pointers for success.");
        b.AppendLine("- If nothing fits, return decision=\"not_found\".");
        b.AppendLine("- Keep excerpt concise and copied from evidence excerpts.");
        return b.ToString();
    }

    private static string BuildFinalizerUserMessage(string taskDescription, string evidenceJson, bool cursorComplete, int stepsUsed, string? afterPointer)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Task description:");
        builder.AppendLine(taskDescription);
        builder.AppendLine("");
        builder.AppendLine("Evidence (JSON):");
        builder.AppendLine(evidenceJson);
        builder.AppendLine("");
        builder.AppendLine($"cursorComplete: {cursorComplete}");
        builder.AppendLine($"stepsUsed: {stepsUsed}");
        builder.AppendLine($"afterPointer: {afterPointer ?? "<none>"}");
        builder.AppendLine("Return a single JSON object per schema.");
        return builder.ToString();
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

            var newEvidence = root.TryGetProperty("newEvidence", out var evidenceElement) && evidenceElement.ValueKind == JsonValueKind.Array
                ? ParseEvidenceArray(evidenceElement)
                : null;

            string? progress = null;
            if (root.TryGetProperty("progress", out var progressElement) && progressElement.ValueKind == JsonValueKind.String)
            {
                progress = progressElement.GetString();
            }

            var needMoreContext = root.TryGetProperty("needMoreContext", out var needElement) && needElement.ValueKind == JsonValueKind.True
                ? true
                : false;

            return new AgentCommand(decision!, newEvidence, progress, needMoreContext) { RawContent = content };
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static FinalizerResponse? ParseFinalizer(string content)
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

            var semanticPointerFrom = root.TryGetProperty("semanticPointerFrom", out var pointerElement) && pointerElement.ValueKind == JsonValueKind.String
                ? pointerElement.GetString()
                : null;

            var excerpt = root.TryGetProperty("excerpt", out var excerptElement) && excerptElement.ValueKind == JsonValueKind.String
                ? excerptElement.GetString()
                : null;

            var whyThis = root.TryGetProperty("whyThis", out var whyElement) && whyElement.ValueKind == JsonValueKind.String
                ? whyElement.GetString()
                : null;

            var markdown = root.TryGetProperty("markdown", out var markdownElement) && markdownElement.ValueKind == JsonValueKind.String
                ? markdownElement.GetString()
                : null;

            var summary = root.TryGetProperty("summary", out var summaryElement) && summaryElement.ValueKind == JsonValueKind.String
                ? summaryElement.GetString()
                : null;

            return new FinalizerResponse(decision!, semanticPointerFrom, excerpt, whyThis, markdown, summary) { RawContent = content };
        }
        catch (Exception)
        {
            return null;
        }
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

    private static IReadOnlyList<EvidenceItem> NormalizeEvidence(IReadOnlyList<EvidenceItem> evidence, CursorPortionView? portion)
    {
        if (evidence.Count == 0 || portion == null)
        {
            return evidence;
        }

        var normalized = new List<EvidenceItem>(evidence.Count);
        foreach (var item in evidence)
        {
            var excerpt = string.IsNullOrWhiteSpace(item.Excerpt) ? TryFindMarkdown(item.Pointer, portion) : item.Excerpt;
            normalized.Add(new EvidenceItem(item.Pointer, excerpt, item.Reason));
        }

        return normalized;
    }

    private static string SerializeEvidence(IReadOnlyList<EvidenceItem> evidence)
    {
        var options = new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = false
        };

        return JsonSerializer.Serialize(evidence, options);
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
        logger.LogDebug("cursor_agent_raw_len: step={Step}, len={Len}", step, content.Length);
        logger.LogDebug("cursor_agent_raw_head: step={Step}, head={Head}", step, content[..Math.Min(300, content.Length)]);
        logger.LogDebug("cursor_agent_raw_tail: step={Step}, tail={Tail}", step, content[^Math.Min(300, content.Length)..]);
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

    private sealed record AgentCommand(string Decision, IReadOnlyList<EvidenceItem>? NewEvidence, string? Progress, bool NeedMoreContext)
    {
        public string? RawContent { get; init; }

        public AgentCommand WithRawContent(string raw) => this with { RawContent = raw };
    }

    private sealed record FinalizerResponse(string Decision, string? SemanticPointerFrom, string? Excerpt, string? WhyThis, string? Markdown, string? Summary)
    {
        public string? RawContent { get; init; }

        public FinalizerResponse WithRawContent(string raw) => this with { RawContent = raw };
    }
}
