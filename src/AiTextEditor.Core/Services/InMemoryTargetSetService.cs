using AiTextEditor.Core.Model;

namespace AiTextEditor.Core.Services;

public class InMemoryTargetSetService
{
    private readonly Dictionary<string, TargetSet> store = new(StringComparer.OrdinalIgnoreCase);

    public TargetSet Create(IEnumerable<LinearItem> items, string? userCommand = null, string? label = null)
    {
        ArgumentNullException.ThrowIfNull(items);

        var itemList = items.ToList();
        if (itemList.Any(item => item == null))
        {
            throw new InvalidOperationException("Target set items cannot contain null entries.");
        }

        var targets = itemList
            .Select(item => new TargetRef(
                Guid.NewGuid().ToString(),
                new SemanticPointer(item.Pointer.Label),
                item.Type,
                item.Markdown,
                item.Text))
            .ToList();

        var targetSet = TargetSet.Create(targets, userCommand, label);
        store[targetSet.Id] = targetSet;
        return targetSet;
    }

    public TargetSet? Get(string targetSetId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetSetId);

        return store.TryGetValue(targetSetId, out var targetSet) ? targetSet : null;
    }

    public IReadOnlyList<TargetSet> List()
    {
        return store.Values.ToList();
    }

    public bool Delete(string targetSetId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetSetId);

        return store.Remove(targetSetId);
    }

    public void Clear()
    {
        store.Clear();
    }
}
