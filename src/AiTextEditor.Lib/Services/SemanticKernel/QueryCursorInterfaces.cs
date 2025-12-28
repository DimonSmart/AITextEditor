using System.Threading;
using System.Threading.Tasks;
using AiTextEditor.Lib.Model;

namespace AiTextEditor.Lib.Services.SemanticKernel;

public interface IQueryCursorAgentRuntime
{
    Task<CursorAgentResult> RunAsync(string cursorName, CursorAgentRequest request, CancellationToken cancellationToken = default);
}

public interface IKeywordCursorAgentRuntime
{
    Task<CursorAgentResult> RunAsync(string cursorName, CursorAgentRequest request, CancellationToken cancellationToken = default);
}
