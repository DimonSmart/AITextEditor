using System;
using System.Text;
using AiTextEditor.Lib.Model;
using Microsoft.Extensions.Logging;

namespace AiTextEditor.Lib.Services;

public abstract class FilteredCursorStream : INamedCursorStream
{
    private readonly LinearDocument document;
    private readonly int maxElements;
    private readonly int maxBytes;
    private readonly ILogger? logger;
    private int currentIndex;
    private bool isComplete;

    public bool IsComplete => isComplete;
    public virtual string? FilterDescription => null;

    protected FilteredCursorStream(LinearDocument document, int maxElements, int maxBytes, string? startAfterPointer, ILogger? logger)
    {
        this.document = document ?? throw new ArgumentNullException(nameof(document));
        this.maxElements = maxElements;
        this.maxBytes = maxBytes;
        this.logger = logger;
        currentIndex = ResolveStartIndex(document, startAfterPointer, logger);
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

            var projectedItem = ProjectItem(sourceItem);
            var itemBytes = CalculateSize(projectedItem);

            if (items.Count >= countBudget) break;
            if (items.Count > 0 && byteBudget - itemBytes < 0) break;

            if (items.Count == 0 && byteBudget - itemBytes < 0)
            {
                items.Add(projectedItem);
                nextIndex++;
                byteBudget = 0;
                break;
            }

            items.Add(projectedItem);
            byteBudget -= itemBytes;
            nextIndex++;
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

    protected virtual LinearItem ProjectItem(LinearItem item) => item;

    protected static int CalculateSize(LinearItem item)
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

    protected abstract bool IsMatch(LinearItem item);

    private static int ResolveStartIndex(LinearDocument document, string? startAfterPointer, ILogger? logger)
    {
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

        return startIndex;
    }

    private bool IsWithinBounds(int index) => index >= 0 && index < document.Items.Count;
}
