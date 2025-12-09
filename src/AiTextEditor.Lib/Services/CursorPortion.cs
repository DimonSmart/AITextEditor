using System.Collections.Generic;
using AiTextEditor.Lib.Model;

namespace AiTextEditor.Lib.Services;

public sealed record CursorPortion(string CursorName, IReadOnlyList<LinearItem> Items, bool HasMore);
