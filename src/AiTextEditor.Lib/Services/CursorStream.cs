using AiTextEditor.Lib.Model;
using System.Text;

namespace AiTextEditor.Lib.Services;

public sealed class CursorStream
{
    private readonly LinearDocument document;
    private readonly int maxElements;
    private readonly int maxBytes;
    private int currentIndex;
    private bool isComplete;
    private readonly bool includeContent = true;
    public bool IsComplete => isComplete;

    public CursorStream(LinearDocument document, int maxElements, int maxBytes, string? startAfterPointer)
    {
        this.document = document ?? throw new ArgumentNullException(nameof(document));
        this.maxElements = maxElements;
        this.maxBytes = maxBytes;

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
            var projectedItem = includeContent ? sourceItem : StripText(sourceItem);
            var itemBytes = CalculateSize(projectedItem, includeContent);

            if (items.Count >= countBudget) break;
            if (items.Count > 0 && byteBudget - itemBytes < 0) break;

            items.Add(projectedItem);
            byteBudget -= itemBytes;
            nextIndex = nextIndex + 1;
            if (byteBudget <= 0) break;
        }

        if (items.Count == 0 && IsWithinBounds(nextIndex))
        {
            var sourceItem = document.Items[nextIndex];
            var projectedItem = includeContent ? sourceItem : StripText(sourceItem);
            items.Add(projectedItem);
            nextIndex = nextIndex + 1;
        }

        currentIndex = nextIndex;
        var hasMore = IsWithinBounds(nextIndex);

        if (!hasMore)
        {
            isComplete = true;
        }

        return new CursorPortion(items, hasMore);
    }

    private static int CalculateSize(LinearItem item, bool includeContent)
    {
        var builder = new StringBuilder();
        builder.Append(item.Index);
        builder.Append('|');
        builder.Append(item.Type);

        builder.Append('|');
        builder.Append(item.Pointer.Serialize());

        if (includeContent)
        {
            builder.Append('|');
            builder.Append(item.Markdown);
            builder.Append('|');
            builder.Append(item.Text);
        }

        return Encoding.UTF8.GetByteCount(builder.ToString());
    }

    private static LinearItem StripText(LinearItem item)
    {
        return new LinearItem(item.Index, item.Type, string.Empty, string.Empty, item.Pointer);
    }

    private bool IsWithinBounds(int index)
    {
        return index >= 0 && index < document.Items.Count;
    }
}
