namespace AiTextEditor.Lib.Model;

public enum CursorAgentMode
{
    FirstMatch,
    CollectToTargetSet,
    AggregateSummary
}

public sealed record CursorAgentRequest(
    CursorParameters Parameters,
    bool Forward,
    CursorAgentMode Mode,
    string TaskDescription,
    string? TargetSetId = null,
    int? MaxSteps = null,
    string? TaskId = null,
    AiTextEditor.Lib.Services.SemanticKernel.TaskState? State = null);

public sealed record CursorAgentResult(
    bool Success,
    string? Reason,
    int? FirstItemIndex,
    string? Summary,
    string? TargetSetId,
    string? TaskId = null,
    AiTextEditor.Lib.Services.SemanticKernel.TaskState? State = null,
    string? PointerFrom = null,
    string? PointerTo = null,
    string? Excerpt = null,
    string? WhyThis = null,
    System.Collections.Generic.IReadOnlyList<AiTextEditor.Lib.Services.SemanticKernel.EvidenceItem>? Evidence = null,
    // Deprecated fields kept for backward compatibility if needed, or mapped to new ones
    string? SemanticPointer = null,
    string? Markdown = null,
    double? Confidence = null,
    string? Reasons = null);
