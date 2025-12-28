using System.ComponentModel;
using AiTextEditor.Lib.Services;
using AiTextEditor.Lib.Services.SemanticKernel;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace AiTextEditor.SemanticKernel;

public sealed class KeywordCursorCreationPlugin(
    IDocumentContext documentContext,
    CursorAgentLimits limits,
    ICursorStore cursorStore,
    ILogger<KeywordCursorCreationPlugin> logger)
{
    private readonly IDocumentContext documentContext = documentContext ?? throw new ArgumentNullException(nameof(documentContext));
    private readonly CursorAgentLimits limits = limits ?? throw new ArgumentNullException(nameof(limits));
    private readonly ICursorStore cursorStore = cursorStore ?? throw new ArgumentNullException(nameof(cursorStore));
    private readonly ILogger<KeywordCursorCreationPlugin> logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private int cursorCounter;

    [KernelFunction("create_keyword_cursor")]
    [Description("Create a keyword cursor that yields matching document items in order.")]
    public string CreateKeywordCursor(
        [Description("Keywords to locate in the document. Items match when they contain any of the keywords (logical OR). Use word stems to ensure matching against inflected forms.")] string[] keywords,
        [Description("Whether headings should be included in cursor output. Defaults to true (include everything).")] bool includeHeadings = true)
    {
        ArgumentNullException.ThrowIfNull(keywords);

        var cursorName = $"keyword_cursor_{cursorCounter++}";
        var cursor = new KeywordCursorStream(documentContext.Document, keywords, limits.MaxElements, limits.MaxBytes, null, includeHeadings, logger);

        if (!cursorStore.TryAddCursor(cursorName, cursor))
        {
            throw new InvalidOperationException("keyword_cursor_registry_add_failed");
        }

        logger.LogInformation("keyword_cursor_created: cursor={Cursor}, includeHeadings={IncludeHeadings}", cursorName, includeHeadings);
        logger.LogInformation("create_keyword_cursor: cursor={Cursor}, includeHeadings={IncludeHeadings}", cursorName, includeHeadings);
        return cursorName;
    }
}
