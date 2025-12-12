using AiTextEditor.Lib.Model;
using System.Linq;

namespace AiTextEditor.Lib.Services;

public class LinearDocumentEditor
{
    public LinearDocument Apply(LinearDocument document, IEnumerable<LinearEditOperation> operations)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(operations);

        var items = document.Items.ToList();

        foreach (var operation in operations)
        {
            ArgumentNullException.ThrowIfNull(operation);
            if (operation.Items == null)
            {
                throw new InvalidOperationException("Edit operation items cannot be null.");
            }

            var targetIndex = ResolveIndex(items, operation);

            switch (operation.Action)
            {
                case LinearEditAction.Replace:
                case LinearEditAction.Split:
                    if (operation.Items.Count > 0)
                    {
                        ReplaceRange(items, targetIndex, 1, operation.Items);
                    }
                    break;

                case LinearEditAction.InsertBefore:
                    if (operation.Items.Count > 0)
                    {
                        ReplaceRange(items, targetIndex, 0, operation.Items);
                    }
                    break;

                case LinearEditAction.InsertAfter:
                    if (operation.Items.Count > 0)
                    {
                        ReplaceRange(items, targetIndex + 1, 0, operation.Items);
                    }
                    break;

                case LinearEditAction.Remove:
                    items.RemoveAt(targetIndex);
                    break;

                case LinearEditAction.MergeWithNext:
                    MergeWithNeighbor(items, targetIndex, 1, operation.Items);
                    break;

                case LinearEditAction.MergeWithPrevious:
                    MergeWithNeighbor(items, targetIndex, -1, operation.Items);
                    break;
            }
        }

        var reindexed = Reindex(items);
        var sourceText = MarkdownDocumentRepository.ComposeMarkdown(reindexed);
        return document with { Items = reindexed, SourceText = sourceText };
    }

    private static void MergeWithNeighbor(List<LinearItem> items, int targetIndex, int neighborOffset, IReadOnlyList<LinearItem> replacements)
    {
        var neighborIndex = targetIndex + neighborOffset;
        if (neighborIndex < 0 || neighborIndex >= items.Count)
        {
            throw new InvalidOperationException($"Cannot merge item at {targetIndex} with neighbor {neighborIndex} because the neighbor is out of range.");
        }

        var mergedItem = replacements.FirstOrDefault() ?? Merge(items[targetIndex], items[neighborIndex]);
        var firstIndex = Math.Min(targetIndex, neighborIndex);
        ReplaceRange(items, firstIndex, 2, new[] { mergedItem });
    }

    private static LinearItem Merge(LinearItem first, LinearItem second)
    {
        return first with
        {
            Markdown = string.Join("\n\n", new[] { first.Markdown, second.Markdown }.Where(s => !string.IsNullOrWhiteSpace(s))),
            Text = string.Join("\n\n", new[] { first.Text, second.Text }.Where(s => !string.IsNullOrWhiteSpace(s)))
        };
    }

    private static int ResolveIndex(List<LinearItem> items, LinearEditOperation operation)
    {
        if (operation.TargetPointer != null)
        {
            var serialized = operation.TargetPointer.Serialize();
            var index = items.FindIndex(i => string.Equals(i.Pointer.Serialize(), serialized, StringComparison.Ordinal));
            if (index < 0)
            {
                throw new InvalidOperationException($"Target pointer '{serialized}' does not exist in the current document.");
            }

            return index;
        }

        if (operation.TargetIndex.HasValue && operation.TargetIndex.Value >= 0 && operation.TargetIndex.Value < items.Count)
        {
            return operation.TargetIndex.Value;
        }

        if (operation.TargetIndex.HasValue)
        {
            throw new InvalidOperationException($"Target index {operation.TargetIndex.Value} is out of range.");
        }

        throw new InvalidOperationException("Linear edit operation must define a target pointer or index.");
    }

    private static void ReplaceRange(List<LinearItem> items, int startIndex, int removeCount, IEnumerable<LinearItem> replacements)
    {
        if (startIndex < 0 || startIndex > items.Count)
        {
            throw new InvalidOperationException($"Replacement start index {startIndex} is out of range.");
        }

        if (removeCount > 0 && startIndex < items.Count)
        {
            items.RemoveRange(startIndex, Math.Min(removeCount, items.Count - startIndex));
        }

        items.InsertRange(startIndex, replacements);
    }

    private static IReadOnlyList<LinearItem> Reindex(IReadOnlyList<LinearItem> items)
    {
        var result = new List<LinearItem>(items.Count);
        var nextId = items.Select(i => i.Pointer?.Id ?? i.Id).DefaultIfEmpty(0).Max() + 1;
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var pointer = item.Pointer ?? new SemanticPointer(nextId++, null);
            var id = item.Id > 0 ? item.Id : (pointer.Id > 0 ? pointer.Id : nextId++);
            if (pointer.Id != id)
            {
                pointer = new SemanticPointer(id, pointer.Label);
            }
            result.Add(item with
            {
                Id = id,
                Index = i,
                Pointer = new SemanticPointer(pointer.Id, pointer.Label)
            });
        }

        return result;
    }
}
