using Xunit;
using AiTextEditor.SemanticKernel;
using Microsoft.Extensions.Logging;

namespace AiTextEditor.Domain.Tests;

public sealed class LightPlanExecutorTests
{
    [Fact]
    public async Task PlanTracksGoalAndStopReason()
    {
        var limits = new CursorAgentLimits { DefaultMaxSteps = 5 };
        var logger = new ListLogger<LightPlanExecutor>();
        var executor = new LightPlanExecutor(limits, logger);
        var runner = new StubPlanRunner(new PlanStepOutcome(false, true), new PlanStepOutcome(true, true));

        var result = await executor.PlanAndRunAsync("Collect intro", "find intro", runner);

        Assert.Equal("Collect intro", result.State.Goal);
        Assert.Equal("goal_reached", result.State.StopReason);
        Assert.Contains("\"goal\":\"Collect intro\"", result.State.Serialize());
    }

    [Fact]
    public async Task SchedulerRunsStepsInOrderAndLogs()
    {
        var limits = new CursorAgentLimits { DefaultMaxSteps = 4 };
        var logger = new ListLogger<LightPlanExecutor>();
        var executor = new LightPlanExecutor(limits, logger);
        var runner = new StubPlanRunner(new PlanStepOutcome(false, true), new PlanStepOutcome(false, false));

        var result = await executor.PlanAndRunAsync("Search dialogue", "dialogue search", runner);

        Assert.Equal(new[] { PlanStepType.CreateCursor, PlanStepType.ReadBatch, PlanStepType.Finalize }, runner.Steps.Select(s => s.StepType));
        Assert.Equal("no_more_batches", result.State.StopReason);
        Assert.Contains("Light plan completed", logger.Messages.Last());
    }

    [Fact]
    public async Task ContextGetsPlanMetadata()
    {
        var limits = new CursorAgentLimits { DefaultMaxSteps = 3 };
        var executor = new LightPlanExecutor(limits, new ListLogger<LightPlanExecutor>());
        var runner = new StubPlanRunner(new PlanStepOutcome(false, true), new PlanStepOutcome(false, false));
        var context = new SemanticKernelContext();

        var result = await executor.PlanAndRunAsync("Summarize chapter", "chapter summary", runner);
        context.Goal = result.State.Goal;
        context.PlanState = result.State;
        context.PlanSteps = result.Steps;
        context.PlanSnapshotJson = result.State.Serialize();

        Assert.Equal("Summarize chapter", context.Goal);
        Assert.Equal("no_more_batches", context.PlanState?.StopReason);
        Assert.NotNull(context.PlanSnapshotJson);
    }

    private sealed class StubPlanRunner : IPlanStepRunner
    {
        private readonly Queue<PlanStepOutcome> outcomes;

        public StubPlanRunner(params PlanStepOutcome[] outcomes)
        {
            this.outcomes = new Queue<PlanStepOutcome>(outcomes);
        }

        public List<PlanStep> Steps { get; } = new();

        public Task<PlanStepOutcome> ExecuteAsync(PlanStep step, CancellationToken cancellationToken)
        {
            Steps.Add(step);
            if (outcomes.Count == 0)
            {
                return Task.FromResult(PlanStepOutcome.Continue);
            }

            return Task.FromResult(outcomes.Dequeue());
        }
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }

        private sealed class NullScope : IDisposable
        {
            public static NullScope Instance { get; } = new();

            public void Dispose()
            {
            }
        }
    }
}
