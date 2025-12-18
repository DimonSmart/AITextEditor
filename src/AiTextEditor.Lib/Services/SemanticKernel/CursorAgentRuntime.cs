using AiTextEditor.Lib.Model;
using DimonSmart.AiUtils;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiTextEditor.Lib.Services.SemanticKernel;

public sealed class CursorAgentRuntime
{
    internal const int DefaultMaxSteps = 128;
    internal const int MaxStepsLimit = 512;
    internal const int DefaultMaxFound = 20;
    private const int SnapshotEvidenceLimit = 5;
    private const int MaxSummaryLength = 500;
    private const int MaxExcerptLength = 1000;
    private const int DefaultResponseTokenLimit = 4000;
    private const int MaxElements = 50;
    private const int MaxBytes = 1024 * 2;
    // private const bool IncludeContent = true;

    private readonly DocumentContext documentContext;
    private readonly IChatCompletionService chatService;
    private readonly ILogger<CursorAgentRuntime> logger;

    public CursorAgentRuntime(
        DocumentContext documentContext,
        IChatCompletionService chatService,
        ILogger<CursorAgentRuntime> logger)
    {
        this.documentContext = documentContext ?? throw new ArgumentNullException(nameof(documentContext));
        this.chatService = chatService ?? throw new ArgumentNullException(nameof(chatService));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<CursorAgentResult> RunAsync(CursorAgentRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var maxSteps = DefaultMaxSteps; // Hardcoded limit
        var agentSystemPrompt = BuildAgentSystemPrompt();
        var taskDefinitionPrompt = BuildTaskDefinitionPrompt(request);

        // Always start with empty state
        var state = new CursorAgentState(Array.Empty<EvidenceItem>());

        var afterPointer = request.StartAfterPointer;
        // var cursorComplete = false;
        string? summary = null;
        string? stopReason = null;
        var stepsUsed = 0;

        // Use hardcoded parameters
        var cursor = new CursorStream(documentContext.Document, MaxElements, MaxBytes, afterPointer);

        for (var step = 0; step < maxSteps; step++)
        {
            var portion = cursor.NextPortion();
            if (!portion.Items.Any())
            {
                // cursorComplete = true;
                stopReason = "cursor_complete";
                break;
            }

            var cursorPortionView = CursorPortionView.FromPortion(portion);
            // var pointerLabel = snapshot.Items.FirstOrDefault()?.Pointer ?? "<none>";
            // var snippet = snapshot.HasMore && snapshot.Items.Count > 0 ? Truncate(snapshot.Items[0].Markdown, 200) : string.Empty;
            var eventName = cursorPortionView.HasMore ? "cursor_batch" : "cursor_batch_complete";
            logger.LogDebug("{Event}: count={Count}, hasMore={HasMore}", eventName, cursorPortionView.Items.Count, cursorPortionView.HasMore);

            // if (snapshot.Items.Count > 0)
            // {
            //     afterPointer = snapshot.Items[^1].Pointer;
            // }
            // 
            // if (!portion.HasMore)
            // {
            //     cursorComplete = true;
            // }

            var snapshotMessage = BuildSnapshotMessage(state, step, afterPointer);
            var batchMessage = BuildBatchMessage(cursorPortionView, step);

            var command = await GetNextCommandAsync(agentSystemPrompt, taskDefinitionPrompt, snapshotMessage, batchMessage, cancellationToken, step);
            stepsUsed = step + 1;

            if (command == null)
            {
                logger.LogError("Agent response malformed.");
                throw new InvalidOperationException("Agent response malformed.");
            }

            var updatedSummary = string.IsNullOrWhiteSpace(command.Progress) ? summary : Truncate(command.Progress, MaxSummaryLength);
            var evidenceToAdd = NormalizeEvidence(command.NewEvidence ?? Array.Empty<EvidenceItem>(), cursorPortionView);
            state = evidenceToAdd.Count > 0 ? state.WithEvidence(evidenceToAdd, DefaultMaxFound) : state;
            summary = updatedSummary ?? summary;

            if (ShouldStop(command.Decision, !cursorPortionView.HasMore, stepsUsed, maxSteps, out stopReason))
            {
                break;
            }
        }

        stopReason ??= "max_steps";
        var finalCursorComplete = cursor.IsComplete || string.Equals(stopReason, "cursor_complete", StringComparison.OrdinalIgnoreCase);
        return await BuildResultByFinalizerAsync(request.TaskDescription, state, summary, stopReason, afterPointer, finalCursorComplete, stepsUsed, cancellationToken);
    }

    private async Task<CursorAgentResult> BuildResultByFinalizerAsync(
        string taskDescription,
        CursorAgentState state,
        string? summary,
        string stopReason,
        string? nextAfterPointer,
        bool cursorComplete,
        int stepsUsed,
        CancellationToken cancellationToken)
    {
        if (state.Evidence.Count == 0)
        {
            // Failure case
            return new CursorAgentResult(
                false,
                summary ?? stopReason, // Use summary or stopReason as summary
                null, // SemanticPointerFrom
                null, // Excerpt
                null, // WhyThis
                state.Evidence,
                nextAfterPointer,
                cursorComplete);
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
               summary ?? stopReason,
               null,
               null,
               null,
               state.Evidence,
               nextAfterPointer,
               cursorComplete);
        }

        if (parsed.Decision != "success" || string.IsNullOrWhiteSpace(parsed.SemanticPointerFrom) || !state.Evidence.Any(e => e.Pointer.Equals(parsed.SemanticPointerFrom, StringComparison.OrdinalIgnoreCase)))
        {
            logger.LogWarning("finalizer_pointer_missing_or_invalid");
            return new CursorAgentResult(
               false,
               "finalizer_missing_pointer",
               null,
               null,
               null,
               state.Evidence,
               nextAfterPointer,
               cursorComplete);
        }

        var finalSummary = string.IsNullOrWhiteSpace(parsed.Summary) ? summary : Truncate(parsed.Summary, MaxSummaryLength);

        return new CursorAgentResult(
            true,
            finalSummary,
            parsed.SemanticPointerFrom,
            Truncate(parsed.Excerpt, MaxExcerptLength),
            parsed.WhyThis,
            state.Evidence,
            nextAfterPointer,
            cursorComplete);
    }

    private async Task<AgentCommand?> GetNextCommandAsync(string agentSystemPrompt, string taskDefinitionPrompt, string snapshotMessage, string batchMessage, CancellationToken cancellationToken, int step)
    {
        var history = new ChatHistory();
        history.AddSystemMessage(agentSystemPrompt);
        history.AddUserMessage(taskDefinitionPrompt);
        //history.AddUserMessage(snapshotMessage);
        history.AddUserMessage(batchMessage);

        history.AddUserMessage("IMPORTANT: Scan the batch. If found, return decision='success' with evidence. Output the JSON command at the end.");

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

    private static string BuildBatchMessage(CursorPortionView portion, int step)
    {
        var batch = new
        {
            firstBatch = step == 0,
            hasMore = portion.HasMore,
            items = portion.Items.Select((item, itemIndex) => new
            {
                pointer = item.SemanticPointer,
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
        var payload = new
        {
            type = "task",
            goal = request.TaskDescription,
            context = string.IsNullOrWhiteSpace(request.Context) ? null : request.Context
        };

        var options = new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = false
        };

        return JsonSerializer.Serialize(payload, options);
    }

    private static string BuildAgentSystemPrompt()
    {
        var b = new StringBuilder();
        b.AppendLine("You are CursorAgent, an automated text scanning engine.");
        b.AppendLine("Your ONLY job is to scan the provided 'batch' of text and identify matches for the 'task'.");
        b.AppendLine("You MUST NOT answer the task question directly. You MUST NOT output natural language.");
        b.AppendLine("You MUST output a JSON command to report findings.");
        b.AppendLine("Return exactly ONE JSON object and nothing else.");
        b.AppendLine();

        b.AppendLine("Output schema:");
        b.AppendLine("{");
        b.AppendLine("  \"decision\": \"continue|done|not_found\",");
        b.AppendLine("  \"newEvidence\": [{\"pointer\":\"...\",\"excerpt\":\"...\",\"reason\":\"...\"}]");
        b.AppendLine("}");
        b.AppendLine();

        b.AppendLine("Evidence rules:");
        b.AppendLine("- From the CURRENT batch only.");
        b.AppendLine("- pointer: The value of the 'pointer' field from the matching item.");
        b.AppendLine("- excerpt: The value of the 'markdown' field from the matching item. DO NOT TRANSLATE. DO NOT PARAPHRASE.");
        b.AppendLine("- reason: Explain why this is a match. Prefer narrative events over summaries.");
        b.AppendLine();

        b.AppendLine("Decision policy:");
        b.AppendLine("- Default: decision=\"continue\".");
        b.AppendLine("- decision=\"done\" ONLY when, using all scanned batches so far (previous + current),");
        b.AppendLine("  the correct answer/selection is already determined and scanning further batches cannot change it.");
        b.AppendLine("  If unseen future content could change the correct selection, keep scanning (continue).");
        b.AppendLine("- decision=\"not_found\" ONLY when batch.lastBatch=true AND snapshot.evidenceCount=0 AND you found no candidates in the current batch.");
        b.AppendLine();

        b.AppendLine("IMPORTANT: You may briefly analyze the text (max 2 sentences), but you MUST output the JSON command at the end.");
        b.AppendLine("Output ONLY valid JSON in the final block.");
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
        b.AppendLine("  \"whyThis\": \"...\",");
        b.AppendLine("  \"markdown\": \"...\",");
        b.AppendLine("  \"summary\": \"...\"");
        b.AppendLine("}");
        b.AppendLine("");
        b.AppendLine("Rules:");
        b.AppendLine("- Use provided evidence only; do not invent new pointers.");
        b.AppendLine("- semanticPointerFrom MUST be one of the evidence pointers for success.");
        b.AppendLine("- If nothing fits, return decision=\"not_found\".");
        b.AppendLine();
        b.AppendLine("IMPORTANT: Do not use chain of thought. Do not explain. Do not output reasoning. Output ONLY valid JSON.");
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
            var excerpt = item.Excerpt; // string.IsNullOrWhiteSpace(item.Excerpt) ? TryFindMarkdown(item.Pointer, portion) : item.Excerpt;
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

   // private static string? TryFindMarkdown(string pointer, CursorPortionView? portion)
   // {
   //     if (portion == null)
   //     {
   //         return null;
   //     }
   //
   //     var match = portion.Items.FirstOrDefault(item => item.Pointer.Equals(pointer, StringComparison.OrdinalIgnoreCase));
   //     return match?.Markdown;
   // }

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
            Temperature = 1.0,
            TopP = 1,
            // ResponseFormat = "json_object",
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
