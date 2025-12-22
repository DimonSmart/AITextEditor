using System.ComponentModel;
using AiTextEditor.Lib.Services.SemanticKernel;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace AiTextEditor.SemanticKernel;

public sealed class QueryCursorCreationPlugin(IQueryCursorRegistry cursorRegistry, ILogger<QueryCursorCreationPlugin> logger)
{
    private readonly IQueryCursorRegistry cursorRegistry = cursorRegistry;
    private readonly ILogger<QueryCursorCreationPlugin> logger = logger;

    [KernelFunction("create_query_cursor")]
    [Description("Create a query cursor that yields matching document items in order.")]
    public string CreateQueryCursor([Description("Query text to locate in the document.")] string query)
    {
        var cursorName = cursorRegistry.CreateCursor(query);
        logger.LogInformation("create_query_cursor: cursor={Cursor}", cursorName);
        return cursorName;
    }
}
