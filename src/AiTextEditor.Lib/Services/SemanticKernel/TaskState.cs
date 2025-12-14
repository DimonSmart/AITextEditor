using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace AiTextEditor.Lib.Services.SemanticKernel;

public sealed record TaskLimits(int Step, int MaxSteps, int MaxSeenTail, int MaxFound)
{
    public const int DefaultMaxSeenTail = 200;
    public const int DefaultMaxFound = 20;

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
    bool? Found,
    IReadOnlyList<string> Seen,
    string Progress,
    TaskLimits Limits,
    IReadOnlyList<EvidenceItem> Evidence)
{
    public static TaskState Create(int maxSteps)
    {
        return new(null, Array.Empty<string>(), "not_started", new TaskLimits(0, maxSteps, TaskLimits.DefaultMaxSeenTail, TaskLimits.DefaultMaxFound), Array.Empty<EvidenceItem>());
    }

    public TaskState WithSeen(IEnumerable<string> seen)
    {
        var merged = new List<string>(Seen);
        foreach (var item in seen)
        {
            if (!merged.Any(existing => existing.Equals(item, StringComparison.OrdinalIgnoreCase)))
            {
                merged.Add(item);
            }
        }

        return this with { Seen = merged };
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

    public TaskState WithEvidence(IEnumerable<EvidenceItem> newEvidence)
    {
        var merged = new List<EvidenceItem>(Evidence);
        foreach (var item in newEvidence)
        {
            if (!merged.Any(existing => existing.Pointer.Equals(item.Pointer, StringComparison.OrdinalIgnoreCase)))
            {
                merged.Add(item);
            }
        }

        if (merged.Count > Limits.MaxFound)
        {
            var skip = merged.Count - Limits.MaxFound;
            merged = merged.Skip(skip).ToList();
        }

        return this with { Evidence = merged };
    }
}

public sealed record TaskStateUpdate(
    bool? Found,
    IReadOnlyCollection<string>? Seen,
    string? Progress,
    TaskLimits? Limits);

public sealed record EvidenceItem(string Pointer, string? Excerpt, string? Reason, double? Score);

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
