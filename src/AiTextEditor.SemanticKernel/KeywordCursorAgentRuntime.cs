using System;
using System.Threading;
using AiTextEditor.Lib.Services;
using AiTextEditor.Lib.Services.SemanticKernel;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AiTextEditor.SemanticKernel;

public sealed class KeywordCursorAgentRuntime : NamedCursorAgentRuntimeBase<KeywordCursorStream>, IKeywordCursorAgentRuntime
{
    private readonly IKeywordCursorRegistry cursorRegistry;

    public KeywordCursorAgentRuntime(
        IKeywordCursorRegistry cursorRegistry,
        IChatCompletionService chatService,
        ICursorAgentPromptBuilder promptBuilder,
        ICursorAgentResponseParser responseParser,
        ICursorEvidenceCollector evidenceCollector,
        CursorAgentLimits limits,
        ILogger<KeywordCursorAgentRuntime> logger)
        : base(chatService, promptBuilder, responseParser, evidenceCollector, limits, logger, "keyword_cursor")
    {
        this.cursorRegistry = cursorRegistry ?? throw new ArgumentNullException(nameof(cursorRegistry));
    }

    protected override KeywordCursorStream GetCursor(string cursorName) => cursorRegistry.GetCursor(cursorName);
}
