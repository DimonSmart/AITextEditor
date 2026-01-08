using AiTextEditor.Core.Common;
using AiTextEditor.Core.Services;
using AiTextEditor.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using System.ComponentModel;
using System.Text.Json;

namespace AiTextEditor.Agent;

public sealed class CursorPlugin(
    IDocumentContext documentContext,
    ICursorStore cursorStore,
    CursorAgentLimits limits,
    ILogger<CursorPlugin> logger)
{
    private readonly IDocumentContext documentContext = documentContext ?? throw new ArgumentNullException(nameof(documentContext));
    private readonly ICursorStore cursorStore = cursorStore ?? throw new ArgumentNullException(nameof(cursorStore));
    private readonly CursorAgentLimits limits = limits ?? throw new ArgumentNullException(nameof(limits));
    private readonly ILogger<CursorPlugin> logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private int _cursorCounter;

    [KernelFunction("create_filtered_cursor")]
    [Description("Creates a named streaming cursor that reads the document in portions. This cursor behaves like IEnumerable.Where over a book paragraphs stream: the filter must be stateless and evaluable on a single portion.")]
    public string CreateFilteredCursor(
        [Description("Stateless per-element filter description (like IEnumerable.Where). Must NOT depend on previous portions (no counters, no 'nth occurrence').")]
        string filterDescription,
        [Description("Optional override for max elements per portion. Clamped to server limits.")] int maxElements = -1,
        [Description("Optional override for max bytes per portion. Clamped to server limits.")] int maxBytes = -1,
        [Description("Pointer to start after (optional).")] string startAfterPointer = "",
        [Description("Whether headings should be included in cursor output. Defaults to true (include everything).")] bool includeHeadings = true,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(filterDescription);

        // TODO: Do not do it now!
        // TODO: cursorStore.TryGet(name) -> throw or overwrite
        // TODO: validate startAfterPointer format if you have a parser

        var cursorName = CreateNewCursorName("llm");
        var document = documentContext.Document;

        var actualMaxElements = Math.Min(maxElements > 0 ? maxElements : limits.MaxElements, limits.MaxElements);
        var actualMaxBytes = Math.Min(maxBytes > 0 ? maxBytes : limits.MaxBytes, limits.MaxBytes);
        logger.LogInformation(
            "CreateFilteredCursor: name={Name}, startAfter={StartAfter}, maxElements={MaxElements}, maxBytes={MaxBytes}, filterDescription={filterDescription}, includeHeadings={IncludeHeadings}",
            cursorName, startAfterPointer, actualMaxElements, actualMaxBytes, filterDescription, includeHeadings);

        var cursorStream = new CursorStream(document, actualMaxElements, actualMaxBytes, string.IsNullOrEmpty(startAfterPointer) ? null : startAfterPointer, filterDescription, includeHeadings, logger);

        cursorStore.RegisterCursor(cursorName, cursorStream);

        return cursorName;
    }

    [KernelFunction("create_keyword_cursor")]
    [Description("Create a keyword cursor that yields matching document items in order.")]
    public string CreateKeywordCursor(
        [Description("Keywords to locate in the document. Items match when they contain any of the keywords (logical OR). Provide base forms; inflections are normalized by the system.")] string[] keywords,
        [Description("Whether headings should be included in cursor output. Defaults to true (include everything).")] bool includeHeadings = true)
    {
        ArgumentNullException.ThrowIfNull(keywords);

        var cursorName = CreateNewCursorName("kwd");
        var cursor = new KeywordCursorStream(documentContext.Document, keywords, limits.MaxElements, limits.MaxBytes, null, includeHeadings, logger);

        if (!cursorStore.TryAddCursor(cursorName, cursor))
        {
            throw new InvalidOperationException("keyword_cursor_registry_add_failed");
        }

        logger.LogInformation("keyword_cursor_created: cursor={Cursor}, includeHeadings={IncludeHeadings}", cursorName, includeHeadings);
        logger.LogInformation("create_keyword_cursor: cursor={Cursor}, includeHeadings={IncludeHeadings}", cursorName, includeHeadings);
        return cursorName;
    }

    [KernelFunction("create_full_scan_cursor")]
    [Description("Create a keyword cursor that yields all document items in order.")]
    public string CreateFullScanCursor(
        [Description("Whether headings should be included in cursor output. Defaults to true (include everything).")] bool includeHeadings = true)
    {
        var cursorName = CreateNewCursorName("fsc");
        var cursor = new FullScanCursorStream(documentContext.Document, limits.MaxElements, limits.MaxBytes, null, includeHeadings, logger);

        if (!cursorStore.TryAddCursor(cursorName, cursor))
        {
            throw new InvalidOperationException("full_scan_keyword_cursor_registry_add_failed");
        }

        logger.LogInformation("keyword_cursor_created: cursor={Cursor}, includeHeadings={IncludeHeadings}", cursorName, includeHeadings);
        logger.LogInformation("create_keyword_cursor: cursor={Cursor}, includeHeadings={IncludeHeadings}", cursorName, includeHeadings);
        return cursorName;
    }



    [KernelFunction("read_cursor_batch")]
    [Description("Reads the next portion from an existing cursor and returns items with pointers.")]
    public string ReadCursorBatch(
        [Description("Cursor name created earlier.")] string cursorName)
    {
        if (!cursorStore.TryGetCursor(cursorName, out var cursor) || cursor == null)
        {
            throw new InvalidOperationException($"Cursor '{cursorName}' not found.");
        }

        var portion = cursor.NextPortion();
        var portionView = CursorPortionView.FromPortion(portion);
        var response = new
        {
            items = portionView.Items,
            portionView.HasMore,
            nextAfterPointer = portionView.Items.Count > 0 ? portionView.Items[^1].SemanticPointer : null,
            limits.MaxElements,
            limits.MaxBytes
        };

        logger.LogDebug("read_cursor_batch: cursor={Cursor}, count={Count}, hasMore={HasMore}", cursorName, portionView.Items.Count, portionView.HasMore);

        return JsonSerializer.Serialize(response, SerializationOptions.RelaxedCompact);
    }

    private string CreateNewCursorName(string prefix) => $"{prefix}_cursor_{_cursorCounter++}";
}
