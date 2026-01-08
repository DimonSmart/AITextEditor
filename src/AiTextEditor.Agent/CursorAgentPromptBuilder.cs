using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using AiTextEditor.Core.Common;
using AiTextEditor.Core.Model;
using AiTextEditor.Core.Services;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace AiTextEditor.Agent;

public interface ICursorAgentPromptBuilder
{
    string BuildAgentSystemPrompt();

    string BuildTaskDefinitionPrompt(CursorAgentRequest request);

    string BuildEvidenceSnapshot(CursorAgentState state);

    string BuildBatchMessage(CursorPortionView portion, int step);

    string BuildFinalizerSystemPrompt();

    string BuildFinalizerUserMessage(string taskDescription, string evidenceJson, bool cursorComplete, int stepsUsed, string? afterPointer);

    OpenAIPromptExecutionSettings CreateSettings();
}

public sealed class CursorAgentPromptBuilder(CursorAgentLimits limits) : ICursorAgentPromptBuilder
{
    private static readonly JsonSerializerOptions SnapshotJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public string BuildAgentSystemPrompt() => CursorAgentSystemPrompt;

    public string BuildTaskDefinitionPrompt(CursorAgentRequest request)
    {
        var payload = new
        {
            type = "task",
            orderingGuaranteed = true,
            goal = request.TaskDescription,
            context = string.IsNullOrWhiteSpace(request.Context) ? null : request.Context,
            maxEvidenceCount = request.MaxEvidenceCount
        };

        return JsonSerializer.Serialize(payload, SerializationOptions.RelaxedCompact);
    }

    public string BuildEvidenceSnapshot(CursorAgentState state)
    {
        var recentPointers = state.Evidence
            .Skip(Math.Max(0, state.Evidence.Count - limits.SnapshotEvidenceLimit))
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

    public string BuildBatchMessage(CursorPortionView portion, int step)
    {
        var batch = new
        {
            firstBatch = step == 0,
            hasMoreBatches = portion.HasMore,
            items = portion.Items.Select(item => new
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

    public string BuildFinalizerSystemPrompt() => FinalizerSystemPrompt;

    public string BuildFinalizerUserMessage(string taskDescription, string evidenceJson, bool cursorComplete, int stepsUsed, string? afterPointer)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Task:");
        builder.AppendLine(taskDescription);
        builder.AppendLine();
        builder.AppendLine("Evidence JSON:");
        builder.AppendLine(evidenceJson);
        builder.AppendLine();
        builder.AppendLine($"cursorComplete: {cursorComplete}");
        builder.AppendLine($"stepsUsed: {stepsUsed}");
        builder.AppendLine($"afterPointer: {afterPointer ?? "<none>"}");
        builder.AppendLine();
        builder.AppendLine("Return exactly one JSON object that follows the schema from the system message. Do not add code fences or explanations.");
        return builder.ToString();
    }

    public OpenAIPromptExecutionSettings CreateSettings()
    {
        return new OpenAIPromptExecutionSettings
        {
            Temperature = 0.0,
            TopP = 1,
            MaxTokens = limits.DefaultResponseTokenLimit,
            ExtensionData = new Dictionary<string, object>
            {
                ["options"] = new Dictionary<string, object>
                {
                    ["think"] = false,
                    ["num_predict"] = limits.DefaultResponseTokenLimit,
                }
            }
        };
    }

    // Clarifies output fields (including progress and needMoreContext) and enforces verbatim excerpts so small models match the parser.
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
  "action": "continue|stop",
  "batchFound": true|false,
  "newEvidence": [
    { "pointer": "...", "excerpt": "...", "reason": "..." }
  ],
  "progress": "...",
  "needMoreContext": true|false
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
- progress: optional, <=1 sentence, short summary of what you found in this batch (or leave empty/null if nothing new).
- needMoreContext: set true only if the batch is too short/ambiguous and you must read further before making progress.
- Character mention scope (for character-collection tasks):
  - Count ONLY specific named individuals or stable unique titles tied to a person (e.g., "Professor Zvezdochkin").
  - Ignore generic groups/roles or unnamed speakers (e.g., "shorties", "listeners", "astronomers", "someone").

Scanning Strategy:
- READ THE FULL TEXT of each item. Mentions may be buried in the middle of long paragraphs.
- Report ALL matches found in the batch. Do not try to filter for "the second one" yourself. The agent needs all candidates to count correctly.
- Do not skip items just because they don't look like the "answer". Report ALL matches.
- If a paragraph contains multiple mentions, report it once as evidence. The counting logic will handle the rest.
- If 'maxEvidenceCount' is provided in the task and you have found enough evidence (snapshot.evidenceCount + newEvidence.length >= maxEvidenceCount), set action="stop".
- If you have NOT found enough evidence yet, set action="continue".
- Set batchFound=true if you found ANY new evidence in this batch, otherwise false.

Content preference:
- Prefer Paragraph/ListItem content.
- Ignore headings/metadata unless the task explicitly asks for them.
""";

    // Aligns finalizer fields with the parser (explicit excerpt + markdown) and bans extra text to keep outputs JSON-only.
    private const string FinalizerSystemPrompt = """
You are CursorAgentFinalizer. Given evidence collected by CursorAgent, decide if the task is resolved.

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
  "excerpt": "...",
  "whyThis": "...",
  "markdown": "...",
  "summary": "..."
}

Rules:
- Use provided evidence only; do not invent pointers or excerpts.
- semanticPointerFrom MUST be one of the evidence pointers when decision="success".
- excerpt MUST be copied verbatim from the chosen evidence excerpt.
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
}
