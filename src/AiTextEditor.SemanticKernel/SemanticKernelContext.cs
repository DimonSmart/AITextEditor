using AiTextEditor.Lib.Model;

namespace AiTextEditor.SemanticKernel;

public sealed class SemanticKernelContext
{
    public List<string> UserMessages { get; } = [];

    public string? LastDocumentSnapshot { get; set; }

    public TargetSet? LastTargetSet { get; set; }

    public string? LastAnswer { get; set; }

    public string? LastCommand { get; set; }

    public string? Goal { get; set; }

    public TaskPlanState? PlanState { get; set; }

    public IReadOnlyList<PlanStep> PlanSteps { get; set; } = Array.Empty<PlanStep>();

    public string? PlanSnapshotJson { get; set; }
}
