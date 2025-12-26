using System;
using System.Collections.Generic;
using AiTextEditor.Lib.Services;
using AiTextEditor.Lib.Services.SemanticKernel;
using Microsoft.Extensions.Logging;

namespace AiTextEditor.SemanticKernel;

public sealed class KeywordCursorRegistry : IKeywordCursorRegistry
{
    private readonly IDocumentContext documentContext;
    private readonly CursorAgentLimits limits;
    private readonly ILogger<KeywordCursorRegistry> logger;
    private readonly ICursorStore cursorStore;

    public KeywordCursorRegistry(
        IDocumentContext documentContext,
        CursorAgentLimits limits,
        ICursorStore cursorStore,
        ILogger<KeywordCursorRegistry> logger)
    {
        this.documentContext = documentContext ?? throw new ArgumentNullException(nameof(documentContext));
        this.limits = limits ?? throw new ArgumentNullException(nameof(limits));
        this.cursorStore = cursorStore ?? throw new ArgumentNullException(nameof(cursorStore));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    private int _cursorCounter = 0;
    public string CreateCursor(IEnumerable<string> keywords)
    {
        ArgumentNullException.ThrowIfNull(keywords);

        var cursorName = $"keyword_cursor_{_cursorCounter++}";
        var cursor = new KeywordCursorStream(documentContext.Document, keywords, limits.MaxElements, limits.MaxBytes, null, logger);

        if (!cursorStore.TryAddCursor(cursorName, cursor))
        {
            throw new InvalidOperationException("keyword_cursor_registry_add_failed");
        }

        logger.LogInformation("keyword_cursor_created: cursor={Cursor}", cursorName);
        return cursorName;
    }

    public KeywordCursorStream GetCursor(string cursorName)
    {
        if (!cursorStore.TryGetCursor<KeywordCursorStream>(cursorName, out var cursor) || cursor == null)
        {
            throw new InvalidOperationException($"keyword_cursor_not_found: {cursorName}");
        }

        return cursor;
    }
}
