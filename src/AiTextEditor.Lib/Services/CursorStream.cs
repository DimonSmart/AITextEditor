using System;
using System.Collections.Generic;
using System.Text;
using AiTextEditor.Lib.Model;

namespace AiTextEditor.Lib.Services;

public sealed class CursorStream
{
    private readonly LinearDocument document;
    private readonly CursorState state;

    public CursorStream(LinearDocument document, CursorParameters parameters, bool forward)
    {
        this.document = document ?? throw new ArgumentNullException(nameof(document));
        ArgumentNullException.ThrowIfNull(parameters);

        var startIndex = forward ? 0 : Math.Max(0, document.Items.Count - 1);
        state = new CursorState(parameters, forward, startIndex);
    }

    public CursorPortion? NextPortion()
    {
        if (state.IsComplete)
        {
            return null;
        }

        if (document.Items.Count == 0)
        {
            state.MarkComplete();
            return new CursorPortion(Array.Empty<LinearItem>(), false);
        }

        var items = new List<LinearItem>();
        var byteBudget = state.Parameters.MaxBytes;
        var countBudget = state.Parameters.MaxElements;
        var nextIndex = state.CurrentIndex;

        while (IsWithinBounds(nextIndex))
        {
            var sourceItem = document.Items[nextIndex];
            var projectedItem = state.Parameters.IncludeContent ? sourceItem : StripText(sourceItem);
            var itemBytes = CalculateSize(projectedItem, state.Parameters.IncludeContent);

            if (items.Count >= countBudget) break;
            if (items.Count > 0 && byteBudget - itemBytes < 0) break;

            items.Add(projectedItem);
            byteBudget -= itemBytes;
            nextIndex = state.IsForward ? nextIndex + 1 : nextIndex - 1;
            if (byteBudget <= 0) break;
        }

        if (items.Count == 0 && IsWithinBounds(nextIndex))
        {
            var sourceItem = document.Items[nextIndex];
            var projectedItem = state.Parameters.IncludeContent ? sourceItem : StripText(sourceItem);
            items.Add(projectedItem);
            nextIndex = state.IsForward ? nextIndex + 1 : nextIndex - 1;
        }

        state.Advance(nextIndex);
        var hasMore = IsWithinBounds(nextIndex);

        if (!hasMore)
        {
            state.MarkComplete();
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
