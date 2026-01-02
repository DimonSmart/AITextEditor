using System.Collections.Generic;
using AiTextEditor.Lib.Model;
using AiTextEditor.Lib.Services;

namespace AiTextEditor.Lib.Services.SemanticKernel;

public interface IDocumentContext
{
    LinearDocument Document { get; }
    IList<string> SpeechQueue { get; }
    CharacterRosterService CharacterRosterService { get; }
}

public interface ICursorAgentRuntime
{
    Task<CursorAgentResult> RunAsync(string cursorName, CursorAgentRequest request, CancellationToken cancellationToken = default);
    Task<CursorAgentStepResult> RunStepAsync(CursorAgentRequest request, CursorPortionView portion, CursorAgentState state, int step, CancellationToken cancellationToken = default);
}
