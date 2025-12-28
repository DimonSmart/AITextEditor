using System.Collections.Concurrent;
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

public sealed record PlanStepOutcome(bool GoalReached, bool HasMore)
{
    public static PlanStepOutcome Continue { get; } = new(false, true);
}

public interface IPlanStepRunner
{
    Task<PlanStepOutcome> ExecuteAsync(PlanStep step, CancellationToken cancellationToken);
}

public sealed record PlanExecutionResult(TaskPlanState State, IReadOnlyList<PlanStep> Steps, string? PlannedStopReason)
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
        var pendingStopReason = PlannedStopReason is null ? "pending" : $"pending:{PlannedStopReason}";
        builder.AppendLine(State.Serialize(pendingStopReason));
        return builder.ToString();
    }
}

public sealed class LightPlanExecutor
{
    private readonly CursorAgentLimits limits;
    private readonly ILogger<LightPlanExecutor> logger;

    public LightPlanExecutor(CursorAgentLimits limits, ILogger<LightPlanExecutor> logger)
    {
        this.limits = limits;
        this.logger = logger;
    }

    public async Task<PlanExecutionResult> PlanAndRunAsync(string goal, string userCommand, IPlanStepRunner? stepRunner = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(goal);
        ArgumentException.ThrowIfNullOrWhiteSpace(userCommand);

        var isSimulation = stepRunner is null;
        var runner = stepRunner ?? new LoggingPlanStepRunner(logger);

        var state = TaskPlanState.Start(goal);
        var steps = new List<PlanStep>();
        logger.LogInformation("Starting light plan: goal='{Goal}', command='{Command}', stepLimit={StepLimit}", goal, userCommand, limits.DefaultMaxSteps);

        var createStep = new PlanStep(state.StepNumber, PlanStepType.CreateCursor, "keyword_cursor-create_keyword_cursor", "cursor-create_cursor");
        steps.Add(createStep);
        await runner.ExecuteAsync(createStep, cancellationToken).ConfigureAwait(false);
        state = state.NextStep();

        var goalReached = false;
        var hasMore = true;
        var stepLimitReached = false;

        while (!goalReached && hasMore && !stepLimitReached)
        {
            if (state.StepNumber >= limits.DefaultMaxSteps)
            {
                stepLimitReached = true;
                break;
            }

            var readStep = new PlanStep(state.StepNumber, PlanStepType.ReadBatch, "chat_cursor_tools-read_cursor_batch");
            steps.Add(readStep);
            var outcome = await runner.ExecuteAsync(readStep, cancellationToken).ConfigureAwait(false);
            goalReached = outcome.GoalReached;
            hasMore = outcome.HasMore;
            state = state.NextStep();
        }

        var stopReason = DetermineStopReason(goalReached, hasMore, stepLimitReached);
        var plannedStopReason = isSimulation ? null : stopReason;
        var finalizeStep = new PlanStep(state.StepNumber, PlanStepType.Finalize, "finalize-answer");
        steps.Add(finalizeStep);
        await runner.ExecuteAsync(finalizeStep, cancellationToken).ConfigureAwait(false);
        state = state.NextStep();

        logger.LogInformation("Light plan completed: {Snapshot}", state.Serialize(plannedStopReason ?? "pending"));
        return new PlanExecutionResult(state, steps, plannedStopReason);
    }

    private static string DetermineStopReason(bool goalReached, bool hasMore, bool stepLimitReached)
    {
        if (goalReached)
        {
            return "goal_reached";
        }

        if (!hasMore)
        {
            return "no_more_batches";
        }

        if (stepLimitReached)
        {
            return "step_limit";
        }

        return "finalized";
    }

    private sealed class LoggingPlanStepRunner : IPlanStepRunner
    {
        private readonly ILogger<LightPlanExecutor> logger;
        private readonly ConcurrentDictionary<PlanStepType, int> stepCount = new();

        public LoggingPlanStepRunner(ILogger<LightPlanExecutor> logger)
        {
            this.logger = logger;
        }

        public Task<PlanStepOutcome> ExecuteAsync(PlanStep step, CancellationToken cancellationToken)
        {
            var count = stepCount.AddOrUpdate(step.StepType, 1, (_, current) => current + 1);
            logger.LogInformation("Plan step {StepNumber}: {StepType} via {Tool} (iteration {Iteration})", step.StepNumber, step.StepType, step.ToolDescription, count);

            return Task.FromResult(step.StepType switch
            {
                PlanStepType.ReadBatch when count > 1 => new PlanStepOutcome(false, false),
                PlanStepType.Finalize => new PlanStepOutcome(true, false),
                _ => PlanStepOutcome.Continue
            });
        }
    }
}
