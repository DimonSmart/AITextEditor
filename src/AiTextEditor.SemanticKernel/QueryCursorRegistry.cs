using System;
using AiTextEditor.Lib.Services;
using AiTextEditor.Lib.Services.SemanticKernel;
using Microsoft.Extensions.Logging;

namespace AiTextEditor.SemanticKernel;

public sealed class QueryCursorRegistry : IQueryCursorRegistry
{
    private readonly IDocumentContext documentContext;
    private readonly CursorAgentLimits limits;
    private readonly ICursorStore cursorStore;
    private readonly ILogger<QueryCursorRegistry> logger;

    public QueryCursorRegistry(
        IDocumentContext documentContext,
        CursorAgentLimits limits,
        ICursorStore cursorStore,
        ILogger<QueryCursorRegistry> logger)
    {
        this.documentContext = documentContext ?? throw new ArgumentNullException(nameof(documentContext));
        this.limits = limits ?? throw new ArgumentNullException(nameof(limits));
        this.cursorStore = cursorStore ?? throw new ArgumentNullException(nameof(cursorStore));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string CreateCursor(string query, bool includeHeadings = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        var cursorName = $"query_cursor_{Guid.NewGuid():N}";
        var cursor = new QueryCursorStream(documentContext.Document, query, limits.MaxElements, limits.MaxBytes, null, includeHeadings, logger);

        if (!cursorStore.TryAddCursor(cursorName, cursor))
        {
            throw new InvalidOperationException("cursor_registry_add_failed");
        }

        logger.LogInformation("query_cursor_created: cursor={Cursor}, includeHeadings={IncludeHeadings}", cursorName, includeHeadings);
        return cursorName;
    }

    public QueryCursorStream GetCursor(string cursorName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cursorName);

        if (!cursorStore.TryGetCursor<QueryCursorStream>(cursorName, out var cursor) || cursor == null)
        {
            throw new InvalidOperationException($"query_cursor_not_found: {cursorName}");
        }

        return cursor;
    }
}
