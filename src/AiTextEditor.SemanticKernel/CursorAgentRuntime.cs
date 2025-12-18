using AiTextEditor.Lib.Model;
using AiTextEditor.Lib.Services;
using AiTextEditor.Lib.Services.SemanticKernel;
using DimonSmart.AiUtils;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiTextEditor.SemanticKernel;

public sealed class CursorAgentRuntime : ICursorAgentRuntime
{
    internal const int DefaultMaxSteps = 128;
    internal const int MaxStepsLimit = 512;
    internal const int DefaultMaxFound = 20;
    private const int SnapshotEvidenceLimit = 5;
    private const int MaxSummaryLength = 500;
    private const int MaxExcerptLength = 1000;
    private const int DefaultResponseTokenLimit = 4000;

    // One cursor batch limits
    private const int MaxElements = 50;
    private const int MaxBytes = 1024 * 8;

    private readonly IDocumentContext documentContext;
    private readonly IChatCompletionService chatService;
    private readonly ILogger<CursorAgentRuntime> logger;

    public CursorAgentRuntime(
        IDocumentContext documentContext,
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
        var cursorAgentState = new CursorAgentState(Array.Empty<EvidenceItem>());

        var afterPointer = request.StartAfterPointer;
        // var cursorComplete = false;
        string? summary = null;
        string? stopReason = null;
        var stepsUsed = 0;

        // Use hardcoded parameters
        var cursor = new CursorStream(documentContext.Document, MaxElements, MaxBytes, afterPointer, logger);

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
            afterPointer = cursorPortionView.Items[^1].SemanticPointer;
            var hasMore = cursorPortionView.HasMore;
            var eventName = cursorPortionView.HasMore ? "cursor_batch" : "cursor_batch_complete";
            logger.LogDebug("{Event}: count={Count}, hasMore={HasMore}", eventName, cursorPortionView.Items.Count, cursorPortionView.HasMore);

            var evidenceSnapshot = BuildEvidenceSnapshot(cursorAgentState);
            var batchMessage = BuildBatchMessage(cursorPortionView, step);

            var command = await GetNextCommandAsync(agentSystemPrompt, taskDefinitionPrompt, evidenceSnapshot, batchMessage, cancellationToken, step);
            stepsUsed = step + 1;

            if (command == null)
            {
                logger.LogError("Agent response malformed.");
                throw new InvalidOperationException("Agent response malformed.");
            }

            var updatedSummary = string.IsNullOrWhiteSpace(command.Progress) ? summary : Truncate(command.Progress, MaxSummaryLength);
            var evidenceToAdd = NormalizeEvidence(command.NewEvidence ?? Array.Empty<EvidenceItem>(), cursorPortionView);
            cursorAgentState = evidenceToAdd.Count > 0 ? cursorAgentState.WithEvidence(evidenceToAdd, DefaultMaxFound) : cursorAgentState;
            summary = updatedSummary ?? summary;

            if (ShouldStop(command.Decision, cursorPortionView.HasMore, stepsUsed, maxSteps, out stopReason))
            {
                break;
            }
        }

        stopReason ??= "max_steps";
        var finalCursorComplete = cursor.IsComplete || string.Equals(stopReason, "cursor_complete", StringComparison.OrdinalIgnoreCase);
        return await BuildResultByFinalizerAsync(request.TaskDescription, cursorAgentState, summary, stopReason, afterPointer, finalCursorComplete, stepsUsed, cancellationToken);
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

    private async Task<AgentCommand?> GetNextCommandAsync(string agentSystemPrompt, string taskDefinitionPrompt, string evidenceSnapshot, string batchMessage, CancellationToken cancellationToken, int step)
    {
        var history = new ChatHistory();
        history.AddSystemMessage(agentSystemPrompt);
        history.AddUserMessage(taskDefinitionPrompt);
        history.AddUserMessage(evidenceSnapshot);
        history.AddUserMessage(batchMessage);

        // history.AddUserMessage("IMPORTANT: Scan the batch. Output the JSON command at the end.");

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



    private static bool ShouldStop(string decisionRaw, bool cursorHasMore, int step, int maxSteps, out string reason)
    {
        var decision = NormalizeDecision(decisionRaw);

        if (decision == "not_found" && cursorHasMore)
        {
            reason = "ignore_not_found_has_more";
            return false;
        }

        if (decision is "done" or "not_found")
        {
            reason = $"decision_{decision}";
            return true;
        }

        if (!cursorHasMore)
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

    private static string NormalizeDecision(string? decision) => decision?.Trim().ToLowerInvariant() switch
    {
        "continue" => "continue",
        "done" => "done",
        "not_found" => "not_found",
        _ => "continue"
    };

    private static string BuildBatchMessage(CursorPortionView portion, int step)
    {
        var batch = new
        {
            firstBatch = step == 0,
            hasMoreBatches = portion.HasMore,
            items = portion.Items.Select((item, itemIndex) => new
            {
                pointer = item.SemanticPointer,
                itemType = item.Type,
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



    private static readonly JsonSerializerOptions SnapshotJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private static string BuildEvidenceSnapshot(CursorAgentState state)
    {
        var recentPointers = state.Evidence
            .Skip(Math.Max(0, state.Evidence.Count - SnapshotEvidenceLimit))
            .Select(e => e.Pointer)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct()
            .ToArray();

        var snapshot = new
        {
            type = "snapshot",
            evidenceCount = state.Evidence.Count,
            recentEvidencePointers = recentPointers
        };

        return JsonSerializer.Serialize(snapshot, SnapshotJsonOptions);
    }


    private static string BuildTaskDefinitionPrompt(CursorAgentRequest request)
    {
        var payload = new
        {
            type = "task",
            orderingGuaranteed = true,
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

    private const string CursorAgentSystemPrompt = """
You are CursorAgent, an automated batch text scanning engine.

Your job:
- Scan ONLY the CURRENT batch.items and extract candidate evidence relevant to the task.
- You MUST NOT answer the task directly.
- You MUST output exactly ONE JSON object and nothing else (no code fences, no extra text).

Input:
- You receive JSON messages that include: task, snapshot, batch.
- snapshot provides:
  - evidenceCount: total number of accepted matches found in PREVIOUS batches
  - recentEvidencePointers: some pointers already returned (may be a subset)
- batch contains items with fields like: pointer, markdown, itemType.

Output schema (JSON):
{
  "decision": "continue|done|not_found",
  "newEvidence": [
    { "pointer": "...", "excerpt": "...", "reason": "..." }
  ]
}

Evidence rules:
- Use ONLY content from the CURRENT batch.
- pointer: COPY EXACTLY from batch.items[].pointer.
- excerpt: COPY EXACTLY from batch.items[].markdown. DO NOT TRANSLATE. DO NOT PARAPHRASE.
- Do NOT add evidence for a pointer that appears in snapshot.recentEvidencePointers.
- reason:
  - 1 sentence max.
  - MUST be local and factual: explain what in the excerpt matches the task.
  - MUST NOT claim any document-wide ordering or position (no "first", "second", "nth", "earlier", "later", "previous", "next", "last").
  - You MAY use snapshot.evidenceCount ONLY for counting/progress decisions, not for describing the excerpt.

Content preference:
- Prefer Paragraph/ListItem content.
- Ignore headings/metadata unless the task explicitly asks for them.

How to use snapshot (IMPORTANT):
- You do NOT see prior batches, but you DO see snapshot which is the only reliable cross-batch memory.
- Treat snapshot.evidenceCount as the number of matches already found before this batch.
- Treat snapshot.recentEvidencePointers as pointers to avoid duplicating results.
- You MUST NOT assume anything else about previous/future text.

Decision policy:
- Default: decision="continue".

- If the task does NOT depend on global order/count and one match is sufficient
  (e.g., "does it mention X", "find any occurrence of X", "find a paragraph about X"):
  - If you found at least 1 valid newEvidence in the CURRENT batch, you MAY set decision="done".

- If the task depends on order or counts (e.g., "first/second/Nth", "Nth mention", "earliest", "count the mentions"):
  - You MAY use snapshot.evidenceCount as the number already found.
  - Try to infer the required target count from task.goal ONLY when it is explicit:
    - examples: "first" -> target=1, "second" -> target=2, "3rd" -> target=3, "Nth" with a number -> that number.
    - If no explicit target number can be inferred, keep scanning until the end.
  - If you inferred target >= 1:
    - Let prev = snapshot.evidenceCount.
    - Let add = number of NEW valid matches you are returning in newEvidence (after de-dup).
    - If prev + add >= target, you MAY set decision="done".
    - Otherwise decision="continue".
  - For "last/latest/final" tasks: do NOT set done early, keep scanning until the end.

- decision="not_found" ONLY when hasMoreBatches=false AND snapshot.evidenceCount=0 AND you found no candidates in the current batch.

IMPORTANT:
- If any instruction conflicts with the decision enum above, ignore it.
- Output JSON ONLY.
""";


    private static string BuildAgentSystemPrompt() => CursorAgentSystemPrompt;


    private const string FinalizerSystemPrompt = """
You are the Finalizer, the final decision maker for the scan results.

You receive:
- The original task
- Aggregated evidence collected across batches (may be incomplete unless the scan finished)

You MUST:
- Respond with exactly ONE JSON object.
- No code fences. No extra text. JSON only.

Schema (JSON):
{
  "decision": "success|not_found",
  "semanticPointerFrom": "...",
  "whyThis": "...",
  "markdown": "...",
  "summary": "..."
}

Rules:
- Use provided evidence only; do not invent pointers or excerpts.
- semanticPointerFrom MUST be one of the evidence pointers when decision="success".
- markdown MUST be copied from the chosen evidence excerpt (verbatim).
- Treat evidence.reason as local rationale only. It is NOT proof of document-wide ordering.

Ordering and ordinal tasks (generic):
- Do NOT claim "first/second/Nth/earlier/later" unless the inputs explicitly guarantee ordering and completeness.
- If the task requires an ordinal selection ("Nth mention", "first", "last") but ordering/completeness is not guaranteed,
  choose the best matching candidate and describe it neutrally (no ordinal wording), or return decision="not_found".

If nothing fits, return decision="not_found".

IMPORTANT:
- Do not output chain-of-thought or reasoning. Output JSON ONLY.
""";

    private static string BuildFinalizerSystemPrompt() => FinalizerSystemPrompt;


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

    private static IReadOnlyList<EvidenceItem> NormalizeEvidence(IReadOnlyList<EvidenceItem> evidence, CursorPortionView portion)
    {
        if (evidence.Count == 0) return evidence;

        var byPointer = portion.Items.ToDictionary(i => i.SemanticPointer, i => i.Markdown, StringComparer.OrdinalIgnoreCase);

        var normalized = new List<EvidenceItem>(evidence.Count);
        foreach (var item in evidence)
        {
            if (!byPointer.TryGetValue(item.Pointer, out var markdown))
            {
                continue;
            }

            normalized.Add(new EvidenceItem(item.Pointer, markdown, item.Reason));
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
            Temperature = 0.0,
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
