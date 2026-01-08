using AiTextEditor.Core.Model;

namespace AiTextEditor.Core.Services;

public interface INamedCursorStream
{
    CursorPortion NextPortion();
    bool IsComplete { get; }
    string? FilterDescription { get; }
}
