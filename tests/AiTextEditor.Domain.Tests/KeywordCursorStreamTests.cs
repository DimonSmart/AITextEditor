using System;
using System.Collections.Generic;
using AiTextEditor.Lib.Model;
using AiTextEditor.Lib.Services;
using Xunit;

namespace AiTextEditor.Domain.Tests;

public class KeywordCursorStreamTests
{
    [Fact]
    public void KeywordCursorMatchesAnyKeyword()
    {
        var markdown = """
        # Contacts

        Bob called Alice yesterday.

        The phone number is +7 999-123-00-00.

        No matches here.
        """;

        var repository = new MarkdownDocumentRepository();
        var document = repository.LoadFromMarkdown(markdown);
        var cursor = new KeywordCursorStream(document, new[] { "bob", "999-123" }, maxElements: 5, maxBytes: 10_000, startAfterPointer: null);

        var readItems = new List<LinearItem>();

        while (!cursor.IsComplete)
        {
            var portion = cursor.NextPortion();
            readItems.AddRange(portion.Items);
        }

        Assert.Collection(
            readItems,
            item => Assert.Contains("Bob", item.Markdown),
            item => Assert.Contains("999-123", item.Markdown));
    }

    [Fact]
    public void KeywordCursorRequiresAtLeastOneKeyword()
    {
        var repository = new MarkdownDocumentRepository();
        var document = repository.LoadFromMarkdown("# Title");

        Assert.Throws<ArgumentException>(() => new KeywordCursorStream(document, Array.Empty<string>(), 5, 1024, null));
    }
}
