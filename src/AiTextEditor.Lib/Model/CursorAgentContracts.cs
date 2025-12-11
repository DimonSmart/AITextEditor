namespace AiTextEditor.Lib.Model;

public enum CursorAgentMode
{
    FirstMatch,
    CollectToTargetSet,
    AggregateSummary
}

public sealed record CursorAgentRequest(
    string CursorName,
    CursorAgentMode Mode,
    string TaskDescription,
    string? TargetSetId = null,
    int? MaxSteps = null);

public sealed record CursorAgentResult(
    bool Success,
    string? Reason,
    int? FirstItemIndex,
    string? Summary,
    string? TargetSetId);
