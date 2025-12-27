using AiTextEditor.Lib.Services.SemanticKernel;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using System.ComponentModel;

namespace AiTextEditor.SemanticKernel;

public sealed class KeywordCursorCreationPlugin(IKeywordCursorRegistry cursorRegistry, ILogger<KeywordCursorCreationPlugin> logger)
{
    private readonly IKeywordCursorRegistry cursorRegistry = cursorRegistry;
    private readonly ILogger<KeywordCursorCreationPlugin> logger = logger;

    [KernelFunction("create_keyword_cursor")]
    [Description("Create a keyword cursor that yields matching document items in order.")]
    public string CreateKeywordCursor([Description("Keywords to locate in the document. Use word stems to ensure matching against inflected forms.")] string[] keywords)
    {
        var cursorName = cursorRegistry.CreateCursor(keywords);
        logger.LogInformation("create_keyword_cursor: cursor={Cursor}", cursorName);
        return cursorName;
    }
}
