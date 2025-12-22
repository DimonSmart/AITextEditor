using System.Collections.Generic;
using AiTextEditor.Lib.Model;

namespace AiTextEditor.Lib.Services.SemanticKernel;

public interface IDocumentContext
{
    LinearDocument Document { get; }
    IList<string> SpeechQueue { get; }
}

public interface ICursorAgentRuntime
{
    Task<CursorAgentResult> RunAsync(CursorAgentRequest request, CancellationToken cancellationToken = default);
    Task<CursorAgentStepResult> RunStepAsync(CursorAgentRequest request, CursorPortionView portion, CursorAgentState state, int step, CancellationToken cancellationToken = default);
}
