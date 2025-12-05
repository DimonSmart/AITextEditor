using AiTextEditor.Lib.Model;
using AiTextEditor.Lib.Services;
using Xunit;

namespace AiTextEditor.Tests;

public class RealWorldEditingTests
{
    private Document CreateSampleDocument()
    {
        return new Document
        {
            Blocks = new List<Block>
            {
                new Block { Id = "title", Type = BlockType.Heading, Level = 1, PlainText = "The Lost City", Markdown = "# The Lost City" },
                new Block { Id = "p1", Type = BlockType.Paragraph, PlainText = "It was a dark and stormy night.", Markdown = "It was a dark and stormy night." },
                new Block { Id = "p2", Type = BlockType.Paragraph, PlainText = "The detective looked at the map.", Markdown = "The detective looked at the map." },
                new Block { Id = "code1", Type = BlockType.Code, PlainText = "print('Hello')", Markdown = "```\nprint('Hello')\n```" },
                new Block { Id = "note1", Type = BlockType.Quote, PlainText = "TODO: Fix this chapter", Markdown = "> TODO: Fix this chapter" }
            }
        };
    }

    [Fact]
    public void Scenario_RewriteIntroduction_ChangesTone()
    {
        // User Request: "Make the first paragraph more dramatic"
        // LLM Decision: Replace block 'p1' with new text.
        
        var doc = CreateSampleDocument();
        var editor = new DocumentEditor();

        var newBlock = new Block 
        { 
            Id = "p1_v2", 
            Type = BlockType.Paragraph, 
            PlainText = "Thunder crashed overhead as the rain lashed against the windowpane.", 
            Markdown = "Thunder crashed overhead as the rain lashed against the windowpane." 
        };

        var op = new EditOperation
        {
            Action = EditActionType.Replace,
            TargetBlockId = "p1",
            NewBlock = newBlock
        };

        editor.ApplyEdits(doc, new[] { op });

        var p1 = doc.Blocks.First(b => b.Type == BlockType.Paragraph);
        Assert.Equal("Thunder crashed overhead as the rain lashed against the windowpane.", p1.PlainText);
        // Verify position didn't change (it's still after title)
        Assert.Equal(1, doc.Blocks.IndexOf(p1));
    }

    [Fact]
    public void Scenario_AddExplanationBeforeCode()
    {
        // User Request: "Explain what the code does before showing it"
        // LLM Decision: Insert paragraph before 'code1'
        
        var doc = CreateSampleDocument();
        var editor = new DocumentEditor();

        var explanation = new Block 
        { 
            Id = "expl1", 
            Type = BlockType.Paragraph, 
            PlainText = "Here is the greeting script:", 
            Markdown = "Here is the greeting script:" 
        };

        var op = new EditOperation
        {
            Action = EditActionType.InsertBefore,
            TargetBlockId = "code1",
            NewBlock = explanation
        };

        editor.ApplyEdits(doc, new[] { op });

        var codeIndex = doc.Blocks.FindIndex(b => b.Id == "code1");
        var explIndex = doc.Blocks.FindIndex(b => b.Id == "expl1");

        Assert.True(explIndex < codeIndex, "Explanation should be before code");
        Assert.Equal(codeIndex - 1, explIndex);
    }

    [Fact]
    public void Scenario_RemoveDraftNotes()
    {
        // User Request: "Delete the TODO note at the end"
        // LLM Decision: Remove block 'note1'
        
        var doc = CreateSampleDocument();
        var editor = new DocumentEditor();

        var op = new EditOperation
        {
            Action = EditActionType.Remove,
            TargetBlockId = "note1"
        };

        editor.ApplyEdits(doc, new[] { op });

        Assert.DoesNotContain(doc.Blocks, b => b.Id == "note1");
        Assert.Equal(4, doc.Blocks.Count);
    }

    [Fact]
    public void Scenario_ComplexEdit_RefactorChapter()
    {
        // User Request: "Replace the second paragraph with two new ones describing the map and the room."
        // LLM Decision: Replace 'p2' with 'p2_a', then InsertAfter 'p2_a' with 'p2_b'.
        
        var doc = CreateSampleDocument();
        var editor = new DocumentEditor();

        var p2_part1 = new Block { Id = "p2_a", Type = BlockType.Paragraph, PlainText = "The map was old and crumbling.", Markdown = "The map was old and crumbling." };
        var p2_part2 = new Block { Id = "p2_b", Type = BlockType.Paragraph, PlainText = "The room was silent.", Markdown = "The room was silent." };

        var ops = new List<EditOperation>
        {
            new EditOperation { Action = EditActionType.Replace, TargetBlockId = "p2", NewBlock = p2_part1 },
            new EditOperation { Action = EditActionType.InsertAfter, TargetBlockId = "p2_a", NewBlock = p2_part2 }
        };

        editor.ApplyEdits(doc, ops);

        Assert.Contains(doc.Blocks, b => b.Id == "p2_a");
        Assert.Contains(doc.Blocks, b => b.Id == "p2_b");
        Assert.DoesNotContain(doc.Blocks, b => b.Id == "p2");
        
        var indexA = doc.Blocks.FindIndex(b => b.Id == "p2_a");
        var indexB = doc.Blocks.FindIndex(b => b.Id == "p2_b");
        
        Assert.Equal(indexA + 1, indexB);
    }
}