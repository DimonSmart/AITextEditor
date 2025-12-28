using System.Text;
using Microsoft.Extensions.Logging;

namespace AiTextEditor.SemanticKernel;

public enum PlanStepType
{
    CreateCursor,
    ReadBatch,
    Finalize
}

public sealed record PlanStep(int StepNumber, PlanStepType StepType, string ToolName, string? ToolFallback = null)
{
    public string ToolDescription => string.IsNullOrWhiteSpace(ToolFallback) ? ToolName : $"{ToolName} (fallback: {ToolFallback})";
}

public sealed record PlanDirective(TaskPlanState State, IReadOnlyList<PlanStep> Steps)
{
    public string BuildPrompt(int maxSteps)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Predefined goal: {State.Goal}");
        builder.AppendLine($"Step limit: {maxSteps}. Stop reasons: goal_reached, no_more_batches, step_limit.");
        builder.AppendLine("Follow the planned steps and keep the same order:");

        var readBatchListed = false;
        foreach (var step in Steps)
        {
            if (step.StepType == PlanStepType.ReadBatch)
            {
                if (readBatchListed)
                {
                    continue;
                }

                readBatchListed = true;
                builder.Append("- ");
                builder.Append(step.StepType);
                builder.Append(" via ");
                builder.Append(step.ToolDescription);
                builder.AppendLine(" (repeat until goal_reached, no_more_batches, or step_limit)");
                continue;
            }

            builder.Append("- ");
            builder.Append(step.StepType);
            builder.Append(" via ");
            builder.AppendLine(step.ToolDescription);
        }

        builder.AppendLine("Final reply must reflect the goal and the final stop reason.");
        builder.Append("Plan snapshot: ");
        builder.AppendLine(State.Serialize("pending"));
        return builder.ToString();
    }
}

public sealed class PlanDirectiveBuilder
{
    private readonly CursorAgentLimits limits;
    private readonly ILogger<PlanDirectiveBuilder> logger;

    public PlanDirectiveBuilder(CursorAgentLimits limits, ILogger<PlanDirectiveBuilder> logger)
    {
        this.limits = limits;
        this.logger = logger;
    }

    public PlanDirective Build(string goal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(goal);

        logger.LogInformation("Building plan directive: goal='{Goal}', stepLimit={StepLimit}", goal, limits.DefaultMaxSteps);

        var steps = new List<PlanStep>
        {
            new(0, PlanStepType.CreateCursor, "cursor-create_keyword_cursor", "cursor-create_filtered_cursor"),
            new(1, PlanStepType.ReadBatch, "cursor-read_cursor_batch"),
            new(2, PlanStepType.Finalize, "finalize-answer")
        };

        var state = TaskPlanState.Start(goal);
        return new PlanDirective(state, steps);
    }
}
