using AiTextEditor.Lib.Services.SemanticKernel;
using System.Collections.Generic;

namespace AiTextEditor.Lib.Model;

public sealed record CursorAgentRequest(
    CursorParameters Parameters,
    string TaskDescription,
    int? MaxSteps = null,
    TaskState? State = null);

public sealed record CursorAgentResult(
    bool Success,
    string? Reason,
    int? FirstItemIndex,
    string? Summary,
    string? TargetSetId,
    TaskState? State = null,
    string? PointerFrom = null,
    string? PointerTo = null,
    string? Excerpt = null,
    string? WhyThis = null,
    IReadOnlyList<EvidenceItem>? Evidence = null,
    // Deprecated fields kept for backward compatibility if needed, or mapped to new ones
    string? SemanticPointer = null,
    string? Markdown = null,
    double? Confidence = null,
    string? Reasons = null);
