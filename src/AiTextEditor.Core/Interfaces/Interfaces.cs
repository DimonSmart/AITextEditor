using System.Collections.Generic;
using AiTextEditor.Core.Model;
using AiTextEditor.Core.Services;

namespace AiTextEditor.Core.Interfaces;

public interface IDocumentContext
{
    LinearDocument Document { get; }
    IList<string> SpeechQueue { get; }
    CharacterDossierService CharacterDossierService { get; }
}

public interface ICursorAgentRuntime
{
    Task<CursorAgentResult> RunAsync(string cursorName, CursorAgentRequest request, CancellationToken cancellationToken = default);
    Task<CursorAgentStepResult> RunStepAsync(CursorAgentRequest request, CursorPortionView portion, CursorAgentState state, int step, CancellationToken cancellationToken = default);
}
