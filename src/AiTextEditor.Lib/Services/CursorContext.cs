using System;
using System.Collections.Generic;
using System.Text;
using AiTextEditor.Lib.Model;

namespace AiTextEditor.Lib.Services;

public sealed class CursorContext
{
    public const string DefaultWholeBookForward = "CUR_WHOLE_BOOK_FORWARD";
    public const string DefaultWholeBookBackward = "CUR_WHOLE_BOOK_BACKWARD";

    private readonly LinearDocument document;
    private readonly Dictionary<string, CursorState> cursors = new(StringComparer.OrdinalIgnoreCase);

    public CursorContext(LinearDocument document)
    {
        this.document = document ?? throw new ArgumentNullException(nameof(document));
        CreateDefaultCursor(DefaultWholeBookForward, CursorDirection.Forward);
        CreateDefaultCursor(DefaultWholeBookBackward, CursorDirection.Backward);
    }

    public IReadOnlyDictionary<string, CursorState> Cursors => cursors;

    public string CreateCursor(string name, CursorParameters parameters, CursorDirection direction)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(parameters);

        var startIndex = direction == CursorDirection.Forward ? 0 : document.Items.Count - 1;
        cursors[name] = new CursorState(name, parameters, direction, startIndex);
        return name;
    }

    public string EnsureWholeBookForward(CursorParameters parameters) => CreateCursor(DefaultWholeBookForward, parameters, CursorDirection.Forward);

    public string EnsureWholeBookBackward(CursorParameters parameters) => CreateCursor(DefaultWholeBookBackward, parameters, CursorDirection.Backward);

    public CursorPortion? GetNextPortion(string cursorName)
    {
        if (!cursors.TryGetValue(cursorName, out var cursor)) return null;
        if (document.Items.Count == 0) return new CursorPortion(cursorName, Array.Empty<LinearItem>(), false);

        var items = new List<LinearItem>();
        var byteBudget = cursor.Parameters.MaxBytes;
        var countBudget = cursor.Parameters.MaxElements;
        var nextIndex = cursor.CurrentIndex;

        while (IsWithinBounds(nextIndex))
        {
            var sourceItem = document.Items[nextIndex];
            var projectedItem = cursor.Parameters.IncludeText ? sourceItem : StripText(sourceItem);
            var itemBytes = CalculateSize(projectedItem, cursor.Parameters.IncludeText);

            if (items.Count >= countBudget) break;
            if (items.Count > 0 && byteBudget - itemBytes < 0) break;

            items.Add(projectedItem);
            byteBudget -= itemBytes;
            nextIndex = cursor.Direction == CursorDirection.Forward ? nextIndex + 1 : nextIndex - 1;
            if (byteBudget <= 0) break;
        }

        if (items.Count == 0 && IsWithinBounds(nextIndex))
        {
            var sourceItem = document.Items[nextIndex];
            var projectedItem = cursor.Parameters.IncludeText ? sourceItem : StripText(sourceItem);
            items.Add(projectedItem);
            nextIndex = cursor.Direction == CursorDirection.Forward ? nextIndex + 1 : nextIndex - 1;
        }

        cursor.Advance(nextIndex);
        var hasMore = IsWithinBounds(nextIndex);
        return new CursorPortion(cursorName, items, hasMore);
    }

    private void CreateDefaultCursor(string name, CursorDirection direction)
    {
        var parameters = new CursorParameters(20, 2048, true);
        CreateCursor(name, parameters, direction);
    }

    private static int CalculateSize(LinearItem item, bool includeText)
    {
        var builder = new StringBuilder();
        builder.Append(item.Index);
        builder.Append('|');
        builder.Append(item.Type);
        builder.Append('|');
        if (item.Level.HasValue)
        {
            builder.Append(item.Level.Value);
        }

        builder.Append('|');
        builder.Append(item.Pointer.Serialize());

        if (includeText)
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
        return new LinearItem(item.Index, item.Type, item.Level, string.Empty, string.Empty, item.Pointer);
    }

    private bool IsWithinBounds(int index)
    {
        return index >= 0 && index < document.Items.Count;
    }
}
