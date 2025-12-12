using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace AiTextEditor.Lib.Services.SemanticKernel;

public sealed record TaskLimits(int Step, int MaxSteps)
{
    public int Remaining => Math.Max(0, MaxSteps - Step);

    public TaskLimits NextStep()
    {
        if (Step >= MaxSteps)
        {
            return this;
        }

        return this with { Step = Step + 1 };
    }
}

public sealed record TaskState(
    string Goal,
    bool? Found,
    IReadOnlyCollection<string> Seen,
    string Progress,
    TaskLimits Limits)
{
    public static TaskState Create(string goal, int maxSteps)
    {
        return new(goal, null, Array.Empty<string>(), "not_started", new TaskLimits(0, maxSteps));
    }

    public TaskState WithSeen(IEnumerable<string> seen)
    {
        var merged = new HashSet<string>(Seen, StringComparer.OrdinalIgnoreCase);
        foreach (var item in seen)
        {
            merged.Add(item);
        }

        return this with { Seen = merged.ToArray() };
    }

    public TaskState WithProgress(string? progress)
    {
        return string.IsNullOrWhiteSpace(progress)
            ? this
            : this with { Progress = progress! };
    }

    public TaskState WithStep(TaskLimits limits)
    {
        return this with { Limits = limits };
    }
}

public sealed record TaskStateUpdate(
    string? Goal,
    bool? Found,
    IReadOnlyCollection<string>? Seen,
    string? Progress,
    TaskLimits? Limits);

public sealed class SessionStore
{
    private readonly Dictionary<string, TaskState> sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock sync = new();

    public TaskState GetOrAdd(string taskId, Func<TaskState> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentNullException.ThrowIfNull(factory);

        using (sync.EnterScope())
        {
            if (!sessions.TryGetValue(taskId, out var state))
            {
                state = factory();
                sessions[taskId] = state;
            }

            return state;
        }
    }

    public void Set(string taskId, TaskState state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentNullException.ThrowIfNull(state);

        using (sync.EnterScope())
        {
            sessions[taskId] = state;
        }
    }

    public bool TryGet(string taskId, out TaskState? state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);

        using (sync.EnterScope())
        {
            return sessions.TryGetValue(taskId, out state);
        }
    }
}
