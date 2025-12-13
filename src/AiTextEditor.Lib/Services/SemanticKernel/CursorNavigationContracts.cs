using System.Collections.Generic;
using System.Linq;
using AiTextEditor.Lib.Model;
using AiTextEditor.Lib.Services;

namespace AiTextEditor.Lib.Services.SemanticKernel;

public sealed record CursorHandle(string Name, bool IsForward, int MaxElements, int MaxBytes, bool IncludeContent);

public sealed record CursorItemView(int Index, string Markdown, string Pointer, string PointerLabel, string Type);

public sealed record CursorPortionView(string CursorName, IReadOnlyList<CursorItemView> Items, bool HasMore)
{
    public static CursorPortionView FromPortion(CursorPortion portion)
    {
        var items = portion.Items
            .Select(item => new CursorItemView(
                item.Index,
                item.Markdown,
                item.Pointer.Serialize(),
                BuildPointerLabel(item),
                item.Type.ToString()))
            .ToList();

        return new CursorPortionView(portion.CursorName, items, portion.HasMore);
    }

    private static string BuildPointerLabel(LinearItem item)
    {
        var baseLabel = !string.IsNullOrWhiteSpace(item.Pointer.Label)
            ? item.Pointer.Label!
            : $"p{item.Index}";

        return $"{item.Pointer.Id}:{baseLabel}";
    }
}
