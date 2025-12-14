using System.Collections.Generic;
using AiTextEditor.Lib.Model;

namespace AiTextEditor.Lib.Services;

public sealed record CursorPortion(IReadOnlyList<LinearItem> Items, bool HasMore);
