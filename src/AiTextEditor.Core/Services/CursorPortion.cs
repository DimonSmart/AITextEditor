using System.Collections.Generic;
using AiTextEditor.Core.Model;

namespace AiTextEditor.Core.Services;

public sealed record CursorPortion(IReadOnlyList<LinearItem> Items, bool HasMore);
