/*
using System;
using System.Threading;
using System.Threading.Tasks;
using AiTextEditor.Lib.Model;
using AiTextEditor.Lib.Services;
using AiTextEditor.Lib.Services.SemanticKernel;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AiTextEditor.SemanticKernel;

public sealed class QueryCursorAgentRuntime : NamedCursorAgentRuntimeBase<QueryCursorStream>, IQueryCursorAgentRuntime
{
    private readonly IQueryCursorRegistry cursorRegistry;

    public QueryCursorAgentRuntime(
        IQueryCursorRegistry cursorRegistry,
        IChatCompletionService chatService,
        ICursorAgentPromptBuilder promptBuilder,
        ICursorAgentResponseParser responseParser,
        ICursorEvidenceCollector evidenceCollector,
        CursorAgentLimits limits,
        ILogger<QueryCursorAgentRuntime> logger)
        : base(chatService, promptBuilder, responseParser, evidenceCollector, limits, logger, "query_cursor")
    {
        this.cursorRegistry = cursorRegistry ?? throw new ArgumentNullException(nameof(cursorRegistry));
    }

    protected override QueryCursorStream GetCursor(string cursorName) => cursorRegistry.GetCursor(cursorName);
}
*/
