using AiTextEditor.SemanticKernel;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AiTextEditor.Domain.Tests;

public sealed class PlanDirectiveBuilderTests
{
    [Fact]
    public void Build_CreatesBaselinePlan()
    {
        var limits = new CursorAgentLimits { DefaultMaxSteps = 2 };
        var builder = new PlanDirectiveBuilder(limits, new LoggerFactory().CreateLogger<PlanDirectiveBuilder>());

        var directive = builder.Build("goal");

        Assert.Null(directive.State.StopReason);
        Assert.Equal(3, directive.Steps.Count);
        Assert.Collection(directive.Steps,
            step =>
            {
                Assert.Equal(0, step.StepNumber);
                Assert.Equal(PlanStepType.CreateCursor, step.StepType);
                Assert.Equal("keyword_cursor-create_keyword_cursor", step.ToolName);
                Assert.Equal("cursor-create_cursor", step.ToolFallback);
            },
            step =>
            {
                Assert.Equal(1, step.StepNumber);
                Assert.Equal(PlanStepType.ReadBatch, step.StepType);
                Assert.Equal("chat_cursor_tools-read_cursor_batch", step.ToolName);
                Assert.Null(step.ToolFallback);
            },
            step =>
            {
                Assert.Equal(2, step.StepNumber);
                Assert.Equal(PlanStepType.Finalize, step.StepType);
                Assert.Equal("finalize-answer", step.ToolName);
                Assert.Null(step.ToolFallback);
            });
    }

    [Fact]
    public void BuildPrompt_UsesPendingStopReasonMarker()
    {
        var limits = new CursorAgentLimits();
        var builder = new PlanDirectiveBuilder(limits, new LoggerFactory().CreateLogger<PlanDirectiveBuilder>());
        var directive = builder.Build("goal");

        var prompt = directive.BuildPrompt(4);

        Assert.Contains("\"stopReason\":\"pending\"", prompt);
    }
}
