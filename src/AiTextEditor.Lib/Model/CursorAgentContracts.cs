using AiTextEditor.Lib.Services.SemanticKernel;
using System.Collections.Generic;

namespace AiTextEditor.Lib.Model;

public sealed record CursorAgentRequest(
    CursorParameters Parameters,
    string TaskDescription,
    int? MaxSteps = null,
    TaskState? State = null);

public sealed record CursorAgentResult(
    /// <summary>
    /// Whether the agent successfully completed the task.
    /// </summary>
    bool Success,

    /// <summary>
    /// Reason for failure or completion.
    /// </summary>
    string? Reason,

    /// <summary>
    /// A brief summary of the findings or the operation.
    /// </summary>
    string? Summary,

    /// <summary>
    /// The ID of the target set where results were collected, if applicable.
    /// </summary>
    string? TargetSetId,

    /// <summary>
    /// The final state of the agent task.
    /// </summary>
    TaskState? State = null,

    /// <summary>
    /// The starting semantic pointer of the found range.
    /// </summary>
    string? SemanticPointerFrom = null,

    /// <summary>
    /// The ending semantic pointer of the found range.
    /// </summary>
    string? SemanticPointerTo = null,

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

    // Deprecated fields kept for backward compatibility if needed, or mapped to new ones
    string? Markdown = null,
    double? Confidence = null,
    string? Reasons = null);
