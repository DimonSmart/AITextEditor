using System;
using System.Collections.Concurrent;
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
    private readonly CursorRegistry mainRegistry;
    private readonly ConcurrentDictionary<string, KeywordCursorStream> cursors = new(StringComparer.OrdinalIgnoreCase);

    public KeywordCursorRegistry(IDocumentContext documentContext, CursorAgentLimits limits, CursorRegistry mainRegistry, ILogger<KeywordCursorRegistry> logger)
    {
        this.documentContext = documentContext ?? throw new ArgumentNullException(nameof(documentContext));
        this.limits = limits ?? throw new ArgumentNullException(nameof(limits));
        this.mainRegistry = mainRegistry ?? throw new ArgumentNullException(nameof(mainRegistry));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string CreateCursor(IEnumerable<string> keywords)
    {
        ArgumentNullException.ThrowIfNull(keywords);

        var cursorName = $"keyword_cursor_{Guid.NewGuid():N}";
        var cursor = new KeywordCursorStream(documentContext.Document, keywords, limits.MaxElements, limits.MaxBytes, null, logger);

        if (!cursors.TryAdd(cursorName, cursor))
        {
            throw new InvalidOperationException("keyword_cursor_registry_add_failed");
        }

        mainRegistry.RegisterCursor(cursorName, cursor);

        logger.LogInformation("keyword_cursor_created: cursor={Cursor}", cursorName);
        return cursorName;
    }

    public KeywordCursorStream GetCursor(string cursorName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cursorName);

        if (!cursors.TryGetValue(cursorName, out var cursor))
        {
            throw new InvalidOperationException($"keyword_cursor_not_found: {cursorName}");
        }

        return cursor;
    }
}
