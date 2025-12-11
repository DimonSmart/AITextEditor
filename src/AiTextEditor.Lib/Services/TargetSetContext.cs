using System.Collections.Concurrent;
using System.Linq;

namespace AiTextEditor.Lib.Services;

public sealed class TargetSetContext
{
    private readonly ConcurrentDictionary<string, HashSet<int>> store = new(StringComparer.OrdinalIgnoreCase);

    public string Create(string? humanReadableName = null)
    {
        var id = string.IsNullOrWhiteSpace(humanReadableName)
            ? $"ts_{Guid.NewGuid():N}"
            : humanReadableName.Trim();

        store[id] = [];
        return id;
    }

    public bool Add(string targetSetId, IEnumerable<int> indices)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetSetId);
        ArgumentNullException.ThrowIfNull(indices);

        if (!store.TryGetValue(targetSetId, out var set))
        {
            return false;
        }

        foreach (var index in indices)
        {
            set.Add(index);
        }

        return true;
    }

    public IReadOnlyList<int>? Get(string targetSetId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetSetId);

        return store.TryGetValue(targetSetId, out var set)
            ? set.Order()
                .ToList()
            : null;
    }
}
