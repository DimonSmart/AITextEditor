using AiTextEditor.Lib.Interfaces;
using AiTextEditor.Lib.Model;
using System.Linq;

namespace AiTextEditor.Lib.Services;

public class InMemoryTargetSetService : ITargetSetService
{
    private readonly Dictionary<string, TargetSet> store = new(StringComparer.OrdinalIgnoreCase);

    public TargetSet Create(
        string documentId,
        IEnumerable<LinearItem> items,
        string? userCommand = null,
        string? label = null,
        Func<LinearItem, string?>? blockIdResolver = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);

        var targetSet = new TargetSet
        {
            DocumentId = documentId,
            UserCommand = userCommand,
            Label = label
        };

        foreach (var item in items)
        {
            targetSet.Targets.Add(ToTargetRef(item, blockIdResolver));
        }

        store[targetSet.Id] = targetSet;
        return targetSet;
    }

    public TargetSet? Get(string targetSetId)
    {
        if (string.IsNullOrWhiteSpace(targetSetId))
        {
            return null;
        }

        return store.TryGetValue(targetSetId, out var targetSet) ? targetSet : null;
    }

    public IReadOnlyList<TargetSet> List(string? documentId = null)
    {
        if (string.IsNullOrWhiteSpace(documentId))
        {
            return store.Values.ToList();
        }

        return store.Values.Where(t => string.Equals(t.DocumentId, documentId, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public bool Delete(string targetSetId)
    {
        if (string.IsNullOrWhiteSpace(targetSetId))
        {
            return false;
        }

        return store.Remove(targetSetId);
    }

    private static TargetRef ToTargetRef(LinearItem item, Func<LinearItem, string?>? blockIdResolver)
    {
        var blockId = blockIdResolver?.Invoke(item);

        return new TargetRef
        {
            BlockId = blockId,
            LinearIndex = item.Index,
            Pointer = new LinearPointer(item.Pointer.Index, new SemanticPointer(item.Pointer.HeadingNumbers, item.Pointer.ParagraphNumber)),
            Type = item.Type,
            Markdown = item.Markdown,
            Text = item.Text
        };
    }
}
