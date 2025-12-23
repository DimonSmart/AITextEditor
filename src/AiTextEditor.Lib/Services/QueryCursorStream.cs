using System;
using AiTextEditor.Lib.Model;
using Microsoft.Extensions.Logging;

namespace AiTextEditor.Lib.Services;

public sealed class QueryCursorStream : FilteredCursorStream
{
    private readonly string query;

    public QueryCursorStream(LinearDocument document, string query, int maxElements, int maxBytes, string? startAfterPointer, ILogger? logger = null)
        : base(document, maxElements, maxBytes, startAfterPointer, logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        this.query = query.Trim();
    }

    protected override bool IsMatch(LinearItem item)
    {
        return item.Text.Contains(query, StringComparison.OrdinalIgnoreCase)
            || item.Markdown.Contains(query, StringComparison.OrdinalIgnoreCase);
    }
}
