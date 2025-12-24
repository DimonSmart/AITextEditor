namespace AiTextEditor.SemanticKernel;

public sealed class CursorAgentLimits
{
    public int DefaultMaxSteps { get; init; } = 128;

    public int MaxStepsLimit { get; init; } = 512;

    public int MaxChatMessages { get; init; } = 50;

    public int DefaultMaxFound { get; init; } = 20;

    public int SnapshotEvidenceLimit { get; init; } = 5;

    public int MaxSummaryLength { get; init; } = 500;

    public int MaxExcerptLength { get; init; } = 1000;

    public int DefaultResponseTokenLimit { get; init; } = 4000;

    public int MaxElements { get; init; } = 3;

    public int MaxBytes { get; init; } = 1024 * 4;
}
