using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AiTextEditor.Lib.Model;
using AiTextEditor.Lib.Services;

namespace AiTextEditor.Lib.Services.SemanticKernel;

public interface IQueryCursorRegistry
{
    string CreateCursor(string query, bool includeHeadings = true);
    QueryCursorStream GetCursor(string cursorName);
}

public interface IQueryCursorAgentRuntime
{
    Task<CursorAgentResult> RunAsync(string cursorName, CursorAgentRequest request, CancellationToken cancellationToken = default);
}

public interface IKeywordCursorRegistry
{
    string CreateCursor(IEnumerable<string> keywords, bool includeHeadings = true);
    KeywordCursorStream GetCursor(string cursorName);
}

public interface IKeywordCursorAgentRuntime
{
    Task<CursorAgentResult> RunAsync(string cursorName, CursorAgentRequest request, CancellationToken cancellationToken = default);
}
