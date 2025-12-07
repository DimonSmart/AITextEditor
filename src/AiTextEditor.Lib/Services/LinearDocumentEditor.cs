using AiTextEditor.Lib.Model;

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
            var targetIndex = ResolveIndex(items, operation);
            if (targetIndex < 0)
            {
                continue;
            }

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
                    if (targetIndex < items.Count)
                    {
                        items.RemoveAt(targetIndex);
                    }
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
        var sourceText = string.Join("\n\n", reindexed.Select(item => item.Markdown));
        return document with { Items = reindexed, SourceText = sourceText };
    }

    private static void MergeWithNeighbor(List<LinearItem> items, int targetIndex, int neighborOffset, IReadOnlyList<LinearItem> replacements)
    {
        var neighborIndex = targetIndex + neighborOffset;
        if (neighborIndex < 0 || neighborIndex >= items.Count)
        {
            return;
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
            var semantic = operation.TargetPointer.SemanticNumber;
            return items.FindIndex(i => string.Equals(i.Pointer.SemanticNumber, semantic, StringComparison.OrdinalIgnoreCase));
        }

        if (operation.TargetIndex.HasValue && operation.TargetIndex.Value >= 0 && operation.TargetIndex.Value < items.Count)
        {
            return operation.TargetIndex.Value;
        }

        return -1;
    }

    private static void ReplaceRange(List<LinearItem> items, int startIndex, int removeCount, IEnumerable<LinearItem> replacements)
    {
        if (startIndex < 0 || startIndex > items.Count)
        {
            return;
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
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var pointer = item.Pointer ?? new LinearPointer(i, new SemanticPointer(Array.Empty<int>(), null));
            result.Add(item with
            {
                Index = i,
                Pointer = new LinearPointer(i, new SemanticPointer(pointer.HeadingNumbers, pointer.ParagraphNumber))
            });
        }

        return result;
    }
}
