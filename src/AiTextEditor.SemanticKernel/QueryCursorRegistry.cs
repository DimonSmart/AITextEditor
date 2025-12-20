using System;
using System.Collections.Concurrent;
using AiTextEditor.Lib.Services;
using AiTextEditor.Lib.Services.SemanticKernel;
using Microsoft.Extensions.Logging;

namespace AiTextEditor.SemanticKernel;

public sealed class QueryCursorRegistry : IQueryCursorRegistry
{
    private readonly IDocumentContext documentContext;
    private readonly CursorAgentLimits limits;
    private readonly ILogger<QueryCursorRegistry> logger;
    private readonly ConcurrentDictionary<string, QueryCursorStream> cursors = new(StringComparer.OrdinalIgnoreCase);

    public QueryCursorRegistry(IDocumentContext documentContext, CursorAgentLimits limits, ILogger<QueryCursorRegistry> logger)
    {
        this.documentContext = documentContext ?? throw new ArgumentNullException(nameof(documentContext));
        this.limits = limits ?? throw new ArgumentNullException(nameof(limits));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string CreateCursor(string query)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        var cursorName = $"query_cursor_{Guid.NewGuid():N}";
        var cursor = new QueryCursorStream(documentContext.Document, query, limits.MaxElements, limits.MaxBytes, null, logger);

        if (!cursors.TryAdd(cursorName, cursor))
        {
            throw new InvalidOperationException("cursor_registry_add_failed");
        }

        logger.LogInformation("query_cursor_created: cursor={Cursor}", cursorName);
        return cursorName;
    }

    public QueryCursorStream GetCursor(string cursorName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cursorName);

        if (!cursors.TryGetValue(cursorName, out var cursor))
        {
            throw new InvalidOperationException($"query_cursor_not_found: {cursorName}");
        }

        return cursor;
    }
}
