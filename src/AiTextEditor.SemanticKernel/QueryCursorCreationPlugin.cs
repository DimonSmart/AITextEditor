using System.ComponentModel;
using AiTextEditor.Lib.Services;
using AiTextEditor.Lib.Services.SemanticKernel;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace AiTextEditor.SemanticKernel;

public sealed class QueryCursorCreationPlugin(
    IDocumentContext documentContext,
    CursorAgentLimits limits,
    ICursorStore cursorStore,
    ILogger<QueryCursorCreationPlugin> logger)
{
    private readonly IDocumentContext documentContext = documentContext ?? throw new ArgumentNullException(nameof(documentContext));
    private readonly CursorAgentLimits limits = limits ?? throw new ArgumentNullException(nameof(limits));
    private readonly ICursorStore cursorStore = cursorStore ?? throw new ArgumentNullException(nameof(cursorStore));
    private readonly ILogger<QueryCursorCreationPlugin> logger = logger ?? throw new ArgumentNullException(nameof(logger));

    [KernelFunction("create_query_cursor")]
    [Description("Create a query cursor that yields matching document items in order.")]
    public string CreateQueryCursor(
        [Description("Query to locate in the document.")] string query,
        [Description("Whether headings should be included in cursor output. Defaults to true (include everything).")] bool includeHeadings = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        var cursorName = $"query_cursor_{Guid.NewGuid():N}";
        var cursor = new QueryCursorStream(documentContext.Document, query, limits.MaxElements, limits.MaxBytes, null, includeHeadings, logger);

        if (!cursorStore.TryAddCursor(cursorName, cursor))
        {
            throw new InvalidOperationException("cursor_registry_add_failed");
        }

        logger.LogInformation("query_cursor_created: cursor={Cursor}, includeHeadings={IncludeHeadings}", cursorName, includeHeadings);
        logger.LogInformation("create_query_cursor: cursor={Cursor}, includeHeadings={IncludeHeadings}", cursorName, includeHeadings);
        return cursorName;
    }
}
