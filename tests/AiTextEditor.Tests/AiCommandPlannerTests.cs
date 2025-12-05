using AiTextEditor.Lib.Model;
using AiTextEditor.Lib.Services;
using AiTextEditor.Tests.Fakes;
using Xunit;

namespace AiTextEditor.Tests;

public class AiCommandPlannerTests
{
    [Fact]
    public async Task PlanAsync_RespectsHeadingContextWhenBuildingOperations()
    {
        var document = new Document
        {
            Blocks =
            [
                new Block { Id = "h_intro", Type = BlockType.Heading, Level = 1, Markdown = "# Intro", PlainText = "Intro" },
                new Block { Id = "p_intro", Type = BlockType.Paragraph, Markdown = "Intro text", PlainText = "Intro text" },
                new Block { Id = "h_setup", Type = BlockType.Heading, Level = 2, Markdown = "## Setup", PlainText = "Setup" },
                new Block { Id = "p_setup", Type = BlockType.Paragraph, Markdown = "Old setup text", PlainText = "Old setup text" },
                new Block { Id = "h_usage", Type = BlockType.Heading, Level = 2, Markdown = "## Usage", PlainText = "Usage" },
                new Block { Id = "p_usage", Type = BlockType.Paragraph, Markdown = "Usage text", PlainText = "Usage text" },
            ]
        };

        var llm = new InspectableLlmClient(prompt =>
        {
            Assert.Contains("h_setup", prompt);
            Assert.Contains("p_setup", prompt);
            Assert.Contains("Setup", prompt);

            return """
            [
              {
                "action": "replace",
                "targetBlockId": "p_setup",
                "blockType": "paragraph",
                "markdown": "Настроено",
                "plainText": "Настроено"
              }
            ]
            """;
        });

        var planner = new AiCommandPlanner(
            new ChunkBuilder(),
            new InMemoryVectorStore(),
            new FunctionCallingLlmEditor(llm));

        var ops = await planner.PlanAsync(document, "В разделе Setup перепиши описание на \"Настроено\".");

        var op = Assert.Single(ops);
        Assert.Equal(EditActionType.Replace, op.Action);
        Assert.Equal("p_setup", op.TargetBlockId);
        Assert.Equal("Настроено", op.NewBlock?.PlainText);
    }

    [Fact]
    public async Task PlanAsync_UsesQuotedFragmentAsAnchorForInsert()
    {
        var document = new Document
        {
            Blocks =
            [
                new Block { Id = "h_ch1", Type = BlockType.Heading, Level = 1, Markdown = "# Chapter 1", PlainText = "Chapter 1" },
                new Block { Id = "p1", Type = BlockType.Paragraph, Markdown = "Intro paragraph", PlainText = "Intro paragraph" },
                new Block { Id = "code1", Type = BlockType.Code, Markdown = "```csharp\n// TODO: rewrite\n```", PlainText = "// TODO: rewrite" },
                new Block { Id = "p2", Type = BlockType.Paragraph, Markdown = "After code", PlainText = "After code" }
            ]
        };

        var llm = new InspectableLlmClient(prompt =>
        {
            Assert.Contains("code1", prompt);
            Assert.Contains("TODO: rewrite", prompt);

            return """
            {
              "operations": [
                {
                  "action": "insert_after",
                  "targetBlockId": "code1",
                  "blockType": "paragraph",
                  "markdown": "Пояснение для читателя.",
                  "plainText": "Пояснение для читателя."
                }
              ]
            }
            """;
        });

        var planner = new AiCommandPlanner(
            new ChunkBuilder(),
            new InMemoryVectorStore(),
            new FunctionCallingLlmEditor(llm));

        var ops = await planner.PlanAsync(document, "После блока 'TODO: rewrite' вставь пояснение для читателя.");

        var op = Assert.Single(ops);
        Assert.Equal(EditActionType.InsertAfter, op.Action);
        Assert.Equal("code1", op.TargetBlockId);
        Assert.Equal("Пояснение для читателя.", op.NewBlock?.PlainText);
    }
}
