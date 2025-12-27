using System.Threading;
using AiTextEditor.SemanticKernel;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AiTextEditor.Domain.Tests;

public sealed class LightPlanExecutorTests
{
    [Fact]
    public async Task PlanAndRunAsync_LeavesStopReasonUnsetUntilCompletion()
    {
        var limits = new CursorAgentLimits { DefaultMaxSteps = 2 };
        var executor = new LightPlanExecutor(limits, new LoggerFactory().CreateLogger<LightPlanExecutor>());
        var result = await executor.PlanAndRunAsync("goal", "command", new AlwaysContinuingRunner());

        Assert.Null(result.State.StopReason);
        Assert.Equal("step_limit", result.PlannedStopReason);
    }

    [Fact]
    public void BuildPrompt_UsesPendingStopReasonMarker()
    {
        var state = new TaskPlanState("goal", 1, null);
        var steps = new[] { new PlanStep(0, PlanStepType.CreateCursor, "cursor-create_cursor") };
        var result = new PlanExecutionResult(state, steps, "no_more_batches");

        var prompt = result.BuildPrompt(4);

        Assert.Contains("\"stopReason\":\"pending:no_more_batches\"", prompt);
    }

    private sealed class AlwaysContinuingRunner : IPlanStepRunner
    {
        public Task<PlanStepOutcome> ExecuteAsync(PlanStep step, CancellationToken cancellationToken)
        {
            return Task.FromResult(new PlanStepOutcome(false, true));
        }
    }
}
