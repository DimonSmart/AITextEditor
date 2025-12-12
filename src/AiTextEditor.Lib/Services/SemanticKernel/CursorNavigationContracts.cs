using System.Collections.Generic;
using System.Linq;
using AiTextEditor.Lib.Model;
using AiTextEditor.Lib.Services;

namespace AiTextEditor.Lib.Services.SemanticKernel;

public sealed record CursorHandle(string Name, bool IsForward, int MaxElements, int MaxBytes, bool IncludeText);

public sealed record CursorItemView(int Index, string Markdown, string Pointer, string PointerLabel, string Type, int? HeadingLevel);

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
                item.Type.ToString(),
                item.Level))
            .ToList();

        return new CursorPortionView(portion.CursorName, items, portion.HasMore);
    }

    private static string BuildPointerLabel(LinearItem item)
    {
        var baseLabel = !string.IsNullOrWhiteSpace(item.Pointer.Label)
            ? item.Pointer.Label!
            : (item.Level.HasValue ? $"H{item.Level.Value}.p{item.Index}" : $"p{item.Index}");

        return $"{item.Pointer.Id}:{baseLabel}";
    }
}
