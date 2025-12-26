using System;
using System.Collections.Generic;
using System.Linq;
using AiTextEditor.Lib.Model;
using Microsoft.Extensions.Logging;

namespace AiTextEditor.Lib.Services;

public sealed class KeywordCursorStream : FilteredCursorStream
{
    private readonly IReadOnlyList<string> keywords;

    public KeywordCursorStream(LinearDocument document, IEnumerable<string> keywords, int maxElements, int maxBytes, string? startAfterPointer, ILogger? logger = null)
        : base(document, maxElements, maxBytes, startAfterPointer, logger)
    {
        ArgumentNullException.ThrowIfNull(keywords);

        var normalized = keywords
            .Select(keyword => keyword?.Trim())
            .Where(keyword => !string.IsNullOrWhiteSpace(keyword))
            .Select(keyword => keyword!)
            .SelectMany(ExpandKeywordVariants)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (normalized.Length == 0)
        {
            throw new ArgumentException("At least one keyword is required.", nameof(keywords));
        }

        this.keywords = normalized;
    }

    public override string? FilterDescription => $"Keywords: {string.Join(", ", keywords)}";

    protected override bool IsMatch(LinearItem item)
    {
        return keywords.Any(keyword =>
            item.Text.Contains(keyword, StringComparison.OrdinalIgnoreCase)
            || item.Markdown.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> ExpandKeywordVariants(string keyword)
    {
        yield return keyword;

        if (keyword.Length < 6)
        {
            yield break;
        }

        const int minLength = 4;
        for (var trim = 1; trim <= 2; trim++)
        {
            var length = keyword.Length - trim;
            if (length < minLength)
            {
                break;
            }

            yield return keyword.Substring(0, length);
        }
    }
}
