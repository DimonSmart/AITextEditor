using AiTextEditor.Core.Model;
using System.Linq;

namespace AiTextEditor.Core.Services;

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
            var targetLabel = operation.TargetPointer.ToCompactString();
            var index = items.FindIndex(i => string.Equals(i.Pointer.ToCompactString(), targetLabel, StringComparison.Ordinal));
            if (index < 0)
            {
                throw new InvalidOperationException($"Target pointer '{targetLabel}' does not exist in the current document.");
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
        var pointerState = new PointerLabelState();
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var label = item.Type == LinearItemType.Heading
                ? pointerState.EnterHeading(ResolveHeadingLevel(item))
                : pointerState.NextPointer();

            result.Add(item with
            {
                Index = i,
                Pointer = new SemanticPointer(label)
            });
        }

        return result;
    }

    private static int ResolveHeadingLevel(LinearItem item)
    {
        if (item.Type != LinearItemType.Heading)
        {
            return 0;
        }

        if (string.IsNullOrWhiteSpace(item.Markdown))
        {
            return 1;
        }

        var trimmed = item.Markdown.AsSpan().TrimStart();
        var level = 0;
        while (level < trimmed.Length && trimmed[level] == '#')
        {
            level++;
        }

        if (level > 0)
        {
            return level;
        }

        var lines = item.Markdown.Split('\n');
        if (lines.Length >= 2)
        {
            var underline = lines[1].Trim();
            if (underline.Length > 0 && underline.All(ch => ch == '='))
            {
                return 1;
            }

            if (underline.Length > 0 && underline.All(ch => ch == '-'))
            {
                return 2;
            }
        }

        return 1;
    }

    private sealed class PointerLabelState
    {
        private readonly List<int> headingCounters = [];
        private int paragraphCounter;

        public string EnterHeading(int headingLevel)
        {
            UpdateHeadingCounters(headingLevel);
            paragraphCounter = 0;
            return string.Join('.', headingCounters);
        }

        public string NextPointer()
        {
            paragraphCounter++;
            var prefix = headingCounters.Count > 0 ? string.Join('.', headingCounters) + "." : string.Empty;
            return $"{prefix}p{paragraphCounter}";
        }

        private void UpdateHeadingCounters(int headingLevel)
        {
            if (headingLevel <= 0)
            {
                headingCounters.Clear();
                headingCounters.Add(1);
                return;
            }

            while (headingCounters.Count < headingLevel)
            {
                headingCounters.Add(0);
            }

            headingCounters[headingLevel - 1]++;
            for (var i = headingLevel; i < headingCounters.Count; i++)
            {
                headingCounters[i] = 0;
            }

            for (var i = headingCounters.Count - 1; i >= 0; i--)
            {
                if (headingCounters[i] != 0) break;
                headingCounters.RemoveAt(i);
            }
        }
    }
}
