using System.Collections.Generic;
using System.Linq;
using AiTextEditor.Lib.Model;
using AiTextEditor.Lib.Services;

namespace AiTextEditor.Lib.Services.SemanticKernel;

public sealed record CursorItemView(int Index, string Markdown, string Pointer, string Type);

public sealed record CursorPortionView(IReadOnlyList<CursorItemView> Items, bool HasMore)
{
    public static CursorPortionView FromPortion(CursorPortion portion)
    {
        var items = portion.Items
            .Select(item => new CursorItemView(
                item.Index,
                item.Markdown,
                item.Pointer.ToCompactString(),
                item.Type.ToString()))
            .ToList();

        return new CursorPortionView(items, portion.HasMore);
    }
}
