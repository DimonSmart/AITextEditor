using AiTextEditor.Lib.Model;
using AiTextEditor.Lib.Services;

namespace AiTextEditor.Lib.Services.SemanticKernel;

public interface ICursorStore
{
    bool TryAddCursor(string name, INamedCursorStream cursor);
    void RegisterCursor(string name, INamedCursorStream cursor);
    bool TryGetCursor(string name, out INamedCursorStream? cursor);
    bool TryGetCursor<TCursor>(string name, out TCursor? cursor)
        where TCursor : class, INamedCursorStream;
    bool TryGetContext(string name, out string? context);
    CursorAgentState GetState(string name);
    void UpdateState(string name, CursorAgentState state);
    int GetStep(string name);
    void IncrementStep(string name);
}
