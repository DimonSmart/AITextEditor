using System.Collections.Generic;

namespace AiTextEditor.Lib.Model;

public sealed record CursorAgentRequest(
    string TaskDescription,
    string? StartAfterPointer = null,
    string? Context = null);

public sealed record CursorAgentResult(
    /// <summary>
    /// Whether the agent successfully completed the task.
    /// </summary>
    bool Success,

    /// <summary>
    /// A brief summary of the findings or the operation.
    /// </summary>
    string? Summary,

    /// <summary>
    /// The starting semantic pointer of the found range.
    /// </summary>
    string? SemanticPointerFrom = null,

    /// <summary>
    /// A text excerpt surrounding the match.
    /// </summary>
    string? Excerpt = null,

    /// <summary>
    /// Explanation of why this result was chosen.
    /// </summary>
    string? WhyThis = null,

    /// <summary>
    /// List of evidence items found during the search.
    /// </summary>
    IReadOnlyList<EvidenceItem>? Evidence = null,

    /// <summary>
    /// Pointer to resume scanning after this run.
    /// </summary>
    string? NextAfterPointer = null,

    /// <summary>
    /// Whether the cursor stream is exhausted.
    /// </summary>
    bool CursorComplete = false);

public sealed record CursorAgentStepResult(
    string Decision,
    IReadOnlyList<EvidenceItem>? NewEvidence,
    string? Progress,
    bool NeedMoreContext,
    bool HasMore);
