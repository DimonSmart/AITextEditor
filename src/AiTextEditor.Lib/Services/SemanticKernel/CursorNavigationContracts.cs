using System.Collections.Generic;
using System.Linq;
using AiTextEditor.Lib.Services;

namespace AiTextEditor.Lib.Services.SemanticKernel;

public sealed record CursorHandle(string Name, bool IsForward, int MaxElements, int MaxBytes, bool IncludeText);

public sealed record CursorItemView(int Index, string Markdown);

public sealed record CursorPortionView(string CursorName, IReadOnlyList<CursorItemView> Items, bool HasMore)
{
    public static CursorPortionView FromPortion(CursorPortion portion)
    {
        var items = portion.Items
            .Select(item => new CursorItemView(item.Index, item.Markdown))
            .ToList();

        return new CursorPortionView(portion.CursorName, items, portion.HasMore);
    }
}
