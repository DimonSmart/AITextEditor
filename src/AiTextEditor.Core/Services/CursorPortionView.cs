using System.Collections.Generic;
using System.Linq;
using AiTextEditor.Core.Model;

namespace AiTextEditor.Core.Services;

public sealed record CursorItemView(string SemanticPointer, string Markdown, string Type);

public sealed record CursorPortionView(IReadOnlyList<CursorItemView> Items, bool HasMore)
{
    public static CursorPortionView FromPortion(CursorPortion portion)
    {
        var items = portion.Items
            .Select(item => new CursorItemView(
                item.Pointer.ToCompactString(),
                item.Markdown,
                item.Type.ToString()))
            .ToList();

        return new CursorPortionView(items, portion.HasMore);
    }
}
