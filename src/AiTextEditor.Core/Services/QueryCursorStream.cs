using System;
using AiTextEditor.Core.Model;
using Microsoft.Extensions.Logging;

namespace AiTextEditor.Core.Services;

public sealed class QueryCursorStream : FilteredCursorStream
{
    private readonly string query;

    public QueryCursorStream(LinearDocument document, string query, int maxElements, int maxBytes, string? startAfterPointer, bool includeHeadings = true, ILogger? logger = null)
        : base(document, maxElements, maxBytes, startAfterPointer, includeHeadings, logger)
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
