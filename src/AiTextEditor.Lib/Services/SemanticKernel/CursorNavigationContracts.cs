using System.Collections.Generic;
using System.Linq;
using AiTextEditor.Lib.Model;
using AiTextEditor.Lib.Services;

namespace AiTextEditor.Lib.Services.SemanticKernel;

public sealed record CursorHandle(string Name, bool IsForward, int MaxElements, int MaxBytes, bool IncludeText);

public sealed record CursorItemView(int Index, string Type, int? Level, string Markdown, string Text, string Pointer);

public sealed record CursorPortionView(string CursorName, bool HasMore, IReadOnlyList<CursorItemView> Items)
{
    public static CursorPortionView FromPortion(CursorPortion portion)
    {
        var items = portion.Items
            .Select(item => new CursorItemView(item.Index, item.Type.ToString(), item.Level, item.Markdown, item.Text, item.Pointer.Serialize()))
            .ToList();

        return new CursorPortionView(portion.CursorName, portion.HasMore, items);
    }
}

public sealed record CursorQueryResponse(string CursorName, bool Success, string? Result);

public sealed record CursorMapResponse(string CursorName, bool Success, IReadOnlyList<PortionTaskResult> Portions);

public sealed record PortionTaskResult(int PortionIndex, string? Result);

public sealed record PointerDescription(string? HeadingTitle, int LineIndex, int CharacterOffset, string Pointer);
