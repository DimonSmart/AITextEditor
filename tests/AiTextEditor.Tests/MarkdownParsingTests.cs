using AiTextEditor.Lib.Model;
using AiTextEditor.Lib.Services;
using Xunit;

namespace AiTextEditor.Tests;

public class MarkdownParsingTests
{
    [Fact]
    public void LoadFromMarkdownFile_ParsesHeadingsAndParagraphs()
    {
        // Arrange
        var markdown = "# Heading 1\n\nParagraph 1\n\n## Heading 2";
        var path = "test.md";
        File.WriteAllText(path, markdown);
        var repo = new MarkdownDocumentRepository();

        // Act
        var document = repo.LoadFromMarkdownFile(path);

        // Assert
        Assert.Equal(3, document.Blocks.Count);
        Assert.Equal(BlockType.Heading, document.Blocks[0].Type);
        Assert.Equal(1, document.Blocks[0].Level);
        Assert.Equal(BlockType.Paragraph, document.Blocks[1].Type);
        Assert.Equal(BlockType.Heading, document.Blocks[2].Type);
        Assert.Equal(2, document.Blocks[2].Level);

        // Cleanup
        File.Delete(path);
    }

    [Fact]
    public void LoadFromMarkdownFile_ParsesListItems()
    {
        // Arrange
        var markdown = "* Item 1\n* Item 2";
        var path = "list_test.md";
        File.WriteAllText(path, markdown);
        var repo = new MarkdownDocumentRepository();

        // Act
        var document = repo.LoadFromMarkdownFile(path);

        // Assert
        Assert.Equal(2, document.Blocks.Count);
        Assert.Equal(BlockType.ListItem, document.Blocks[0].Type);
        Assert.Equal(BlockType.ListItem, document.Blocks[1].Type);

        // Check parent ID (should be same for both items if they are in same list)
        // Note: Our implementation generates a virtual ID for the list container and assigns it as parent.
        Assert.NotNull(document.Blocks[0].ParentId);
        Assert.Equal(document.Blocks[0].ParentId, document.Blocks[1].ParentId);

        // Cleanup
        File.Delete(path);
    }
}
