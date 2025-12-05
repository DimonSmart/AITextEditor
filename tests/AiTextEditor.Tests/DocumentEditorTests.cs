using AiTextEditor.Lib.Model;
using AiTextEditor.Lib.Services;
using Xunit;

namespace AiTextEditor.Tests;

public class DocumentEditorTests
{
    [Fact]
    public void ApplyEdits_InsertAfter_AddsBlock()
    {
        // Arrange
        var editor = new DocumentEditor();
        var block1 = new Block { Id = "1", Type = BlockType.Paragraph, Markdown = "Block 1" };
        var block2 = new Block { Id = "2", Type = BlockType.Paragraph, Markdown = "Block 2" };
        var document = new Document { Blocks = new List<Block> { block1, block2 } };

        var newBlock = new Block { Id = "3", Type = BlockType.Paragraph, Markdown = "New Block" };
        var op = new EditOperation
        {
            Action = EditActionType.InsertAfter,
            TargetBlockId = "1",
            NewBlock = newBlock
        };

        // Act
        editor.ApplyEdits(document, new[] { op });

        // Assert
        Assert.Equal(3, document.Blocks.Count);
        Assert.Equal("1", document.Blocks[0].Id);
        Assert.Equal("3", document.Blocks[1].Id);
        Assert.Equal("2", document.Blocks[2].Id);
    }

    [Fact]
    public void ApplyEdits_Replace_ReplacesBlock()
    {
        // Arrange
        var editor = new DocumentEditor();
        var block1 = new Block { Id = "1", Type = BlockType.Paragraph, Markdown = "Block 1" };
        var document = new Document { Blocks = new List<Block> { block1 } };

        var newBlock = new Block { Id = "1", Type = BlockType.Paragraph, Markdown = "Updated Block 1" };
        var op = new EditOperation
        {
            Action = EditActionType.Replace,
            TargetBlockId = "1",
            NewBlock = newBlock
        };

        // Act
        editor.ApplyEdits(document, new[] { op });

        // Assert
        Assert.Single(document.Blocks);
        Assert.Equal("Updated Block 1", document.Blocks[0].Markdown);
    }

    [Fact]
    public void ApplyEdits_Remove_RemovesBlock()
    {
        // Arrange
        var editor = new DocumentEditor();
        var block1 = new Block { Id = "1", Type = BlockType.Paragraph, Markdown = "Block 1" };
        var document = new Document { Blocks = new List<Block> { block1 } };

        var op = new EditOperation
        {
            Action = EditActionType.Remove,
            TargetBlockId = "1"
        };

        // Act
        editor.ApplyEdits(document, new[] { op });

        // Assert
        Assert.Empty(document.Blocks);
    }
}
