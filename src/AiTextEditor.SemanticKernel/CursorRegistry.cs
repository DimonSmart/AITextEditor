using AiTextEditor.Lib.Model;
using AiTextEditor.Lib.Services;
using AiTextEditor.Lib.Services.SemanticKernel;

namespace AiTextEditor.SemanticKernel;

public class CursorRegistry
{
    private readonly Dictionary<string, INamedCursorStream> cursors = [];
    private readonly Dictionary<string, CursorAgentState> agentStates = [];
    private readonly Dictionary<string, int> agentSteps = [];

    public void RegisterCursor(string name, INamedCursorStream cursor)
    {
        cursors[name] = cursor;
        agentStates[name] = new CursorAgentState(Array.Empty<EvidenceItem>());
        agentSteps[name] = 0;
    }

    public bool TryGetCursor(string name, out INamedCursorStream? cursor) => cursors.TryGetValue(name, out cursor);
    
    public bool TryGetContext(string name, out string? context)
    {
        if (cursors.TryGetValue(name, out var cursor))
        {
            context = cursor.FilterDescription;
            return true;
        }
        context = null;
        return false;
    }
    
    public CursorAgentState GetState(string name) => agentStates[name];
    
    public void UpdateState(string name, CursorAgentState state) => agentStates[name] = state;

    public int GetStep(string name) => agentSteps[name];
    
    public void IncrementStep(string name) => agentSteps[name]++;
}
