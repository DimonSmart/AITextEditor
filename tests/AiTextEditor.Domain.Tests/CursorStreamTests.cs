using AiTextEditor.Lib.Services;
using Xunit;
using System.IO;
using System.Collections.Generic;
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
}
