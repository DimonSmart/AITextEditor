using AiTextEditor.Lib.Services;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using System.ComponentModel;

namespace AiTextEditor.SemanticKernel;

public sealed class CursorPlugin(
    EditorSession editorSession,
    CursorRegistry cursorRegistry,
    CursorAgentLimits limits,
    ILogger<CursorPlugin> logger)
{
    [KernelFunction("create_cursor")]
    [Description("Creates a named streaming cursor that reads the document in portions. This cursor behaves like IEnumerable.Where over a stream: the filter must be stateless and evaluable on a single portion. The cursor does NOT keep counters and cannot perform global queries like '3rd occurrence'.")]
    public string CreateCursor(
        [Description("Unique cursor name. If the name already exists, the call fails.")] string name,
        [Description("Stateless per-element filter description (like IEnumerable.Where). Must NOT depend on previous portions (no counters, no 'nth occurrence').")]
        string filterDescription,

        [Description("Optional override for max elements per portion. Clamped to server limits.")] int maxElements = -1,
        [Description("Optional override for max bytes per portion. Clamped to server limits.")] int maxBytes = -1,
        [Description("Pointer to start after (optional).")] string startAfterPointer = "",
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(filterDescription);

        // TODO: Do not do it now!
        // TODO: registry.TryGet(name) -> throw or overwrite
        // TODO: validate startAfterPointer format if you have a parser

        var document = editorSession.GetDefaultDocument();

        var actualMaxElements = Math.Min(maxElements > 0 ? maxElements : limits.MaxElements, limits.MaxElements);
        var actualMaxBytes = Math.Min(maxBytes > 0 ? maxBytes : limits.MaxBytes, limits.MaxBytes);
        logger.LogInformation(
               "CreateCursor: name={Name}, startAfter={StartAfter}, maxElements={MaxElements}, maxBytes={MaxBytes}, filterDescription={filterDescription}",
               name, startAfterPointer, actualMaxElements, actualMaxBytes, filterDescription);

        var cursorStream = new CursorStream(document, actualMaxElements, actualMaxBytes, string.IsNullOrEmpty(startAfterPointer) ? null : startAfterPointer, filterDescription, logger);

        cursorRegistry.RegisterCursor(name, cursorStream);

        return name;
    }
}
