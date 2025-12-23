using AiTextEditor.Lib.Model;

namespace AiTextEditor.Lib.Services;

public interface INamedCursorStream
{
    CursorPortion NextPortion();
    bool IsComplete { get; }
    string? FilterDescription { get; }
}
