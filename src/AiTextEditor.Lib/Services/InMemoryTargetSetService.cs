using AiTextEditor.Lib.Model;

namespace AiTextEditor.Lib.Services;

public class InMemoryTargetSetService
{
    private readonly Dictionary<string, TargetSet> store = new(StringComparer.OrdinalIgnoreCase);

    public TargetSet Create(string documentId, IEnumerable<LinearItem> items, string? userCommand = null, string? label = null)
    {
        var targets = items
            .Select(item => new TargetRef(
                Guid.NewGuid().ToString(),
                documentId,
                new LinearPointer(item.Pointer.Index, new SemanticPointer(item.Pointer.HeadingNumbers, item.Pointer.ParagraphNumber)),
                item.Type,
                item.Markdown,
                item.Text))
            .ToList();

        var targetSet = TargetSet.Create(documentId, targets, userCommand, label);
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
}
