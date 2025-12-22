using System;
using System.Text;
using AiTextEditor.Lib.Model;
using Microsoft.Extensions.Logging;

namespace AiTextEditor.Lib.Services;

public sealed class QueryCursorStream
{
    private readonly LinearDocument document;
    private readonly int maxElements;
    private readonly int maxBytes;
    private readonly string query;
    private int currentIndex;
    private bool isComplete;
    public bool IsComplete => isComplete;

    public QueryCursorStream(LinearDocument document, string query, int maxElements, int maxBytes, string? startAfterPointer, ILogger? logger = null)
    {
        this.document = document ?? throw new ArgumentNullException(nameof(document));
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        this.query = query.Trim();
        this.maxElements = maxElements;
        this.maxBytes = maxBytes;

        if (!string.IsNullOrEmpty(startAfterPointer))
        {
            if (!SemanticPointer.TryParse(startAfterPointer, out _))
            {
                logger?.LogWarning("Invalid pointer format: {Pointer}", startAfterPointer);
                startAfterPointer = null;
            }
        }

        var startIndex = 0;
        if (!string.IsNullOrEmpty(startAfterPointer))
        {
            var foundIndex = -1;
            for (var i = 0; i < document.Items.Count; i++)
            {
                if (document.Items[i].Pointer.Serialize() == startAfterPointer)
                {
                    foundIndex = i;
                    break;
                }
            }

            if (foundIndex != -1)
            {
                startIndex = foundIndex + 1;
            }
        }

        currentIndex = startIndex;
    }

    public CursorPortion NextPortion()
    {
        if (isComplete || document.Items.Count == 0)
        {
            isComplete = true;
            return new CursorPortion([], false);
        }

        var items = new List<LinearItem>();
        var byteBudget = maxBytes;
        var countBudget = maxElements;
        var nextIndex = currentIndex;

        while (IsWithinBounds(nextIndex))
        {
            var sourceItem = document.Items[nextIndex];
            if (!IsMatch(sourceItem))
            {
                nextIndex++;
                continue;
            }

            var itemBytes = CalculateSize(sourceItem);

            if (items.Count >= countBudget) break;
            if (items.Count > 0 && byteBudget - itemBytes < 0) break;

            if (items.Count == 0 && byteBudget - itemBytes < 0)
            {
                items.Add(sourceItem);
                nextIndex = nextIndex + 1;
                byteBudget = 0;
                break;
            }

            items.Add(sourceItem);
            byteBudget -= itemBytes;
            nextIndex = nextIndex + 1;
            if (byteBudget <= 0) break;
        }

        currentIndex = nextIndex;
        var hasMore = IsWithinBounds(nextIndex);

        if (!hasMore)
        {
            isComplete = true;
        }

        return new CursorPortion(items, hasMore);
    }

    private bool IsMatch(LinearItem item)
    {
        return item.Text.Contains(query, StringComparison.OrdinalIgnoreCase)
            || item.Markdown.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private static int CalculateSize(LinearItem item)
    {
        var builder = new StringBuilder();
        builder.Append(item.Index);
        builder.Append('|');
        builder.Append(item.Type);

        builder.Append('|');
        builder.Append(item.Pointer.ToCompactString());

        builder.Append('|');
        builder.Append(item.Markdown);
        builder.Append('|');

        return Encoding.UTF8.GetByteCount(builder.ToString());
    }

    private bool IsWithinBounds(int index)
    {
        return index >= 0 && index < document.Items.Count;
    }
}
