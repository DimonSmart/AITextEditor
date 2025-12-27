using System.Text.Json;
using AiTextEditor.Lib.Common;

namespace AiTextEditor.SemanticKernel;

public sealed record TaskPlanState(string Goal, int StepNumber, string? StopReason)
{
    public bool IsCompleted => !string.IsNullOrWhiteSpace(StopReason);

    public static TaskPlanState Start(string goal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(goal);
        return new TaskPlanState(goal, 0, null);
    }

    public TaskPlanState NextStep() => new(Goal, StepNumber + 1, StopReason);

    public TaskPlanState WithStopReason(string stopReason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stopReason);
        return new TaskPlanState(Goal, StepNumber, stopReason);
    }

    public string Serialize()
    {
        var payload = new
        {
            goal = Goal,
            step = StepNumber,
            stopReason = StopReason
        };

        return JsonSerializer.Serialize(payload, SerializationOptions.RelaxedCompact);
    }
}
