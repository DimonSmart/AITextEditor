using AiTextEditor.Lib.Services;
using Xunit;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using AiTextEditor.Lib.Model;

namespace AiTextEditor.Domain.Tests;

public class CursorStreamTests
{
    [Theory]
    [InlineData("neznayka_sample.md")]
    public void CursorReadsAllItemsFromNeznaykaSample(string fileName)
    {
        // Arrange
        var filePath = Path.Combine("Fixtures", "MarkdownBooks", fileName);
        Assert.True(File.Exists(filePath), $"File not found: {filePath}");

        var repository = new MarkdownDocumentRepository();
        var document = repository.LoadFromMarkdownFile(filePath);
        
        var cursorStream = new CursorStream(document, maxElements: 10, maxBytes: 10000);

        var readItems = new List<LinearItem>();

        // Act
        while (!cursorStream.IsComplete)
        {
            var portion = cursorStream.NextPortion();
            readItems.AddRange(portion.Items);
        }

        // Assert
        Assert.Equal(document.Items.Count, readItems.Count);
        for (var i = 0; i < document.Items.Count; i++)
        {
            Assert.Equal(document.Items[i].Pointer.ToCompactString(), readItems[i].Pointer.ToCompactString());
        }
    }

    [Fact]
    public void CursorSkipsHeadingsWhenConfigured()
    {
        var markdown = """
        # Title

        Paragraph one.

        ## Subtitle

        Paragraph two.
        """;

        var repository = new MarkdownDocumentRepository();
        var document = repository.LoadFromMarkdown(markdown);

        var cursorStream = new CursorStream(document, maxElements: 10, maxBytes: 10000, includeHeadings: false);

        var readItems = new List<LinearItem>();
        while (!cursorStream.IsComplete)
        {
            var portion = cursorStream.NextPortion();
            readItems.AddRange(portion.Items);
        }

        var expectedItems = document.Items.Where(item => item.Type != LinearItemType.Heading).ToList();
        Assert.All(readItems, item => Assert.NotEqual(LinearItemType.Heading, item.Type));
        Assert.Equal(expectedItems.Count, readItems.Count);
        for (var i = 0; i < expectedItems.Count; i++)
        {
            Assert.Equal(expectedItems[i].Pointer.ToCompactString(), readItems[i].Pointer.ToCompactString());
        }
    }
}
