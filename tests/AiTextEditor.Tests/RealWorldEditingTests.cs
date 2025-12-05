using AiTextEditor.Lib.Model;
using AiTextEditor.Lib.Services;
using Xunit;

namespace AiTextEditor.Tests;

public class RealWorldEditingTests
{
    private const string ExplanationText = "Here is what the snippet does before we dive into the code.";
    private readonly MarkdownBlockFactory blockFactory;
    private readonly MarkdownDocumentRepository repository;

    public RealWorldEditingTests()
        : this(new MarkdownDocumentRepository())
    {
    }

    private RealWorldEditingTests(MarkdownDocumentRepository repository)
    {
        this.repository = repository;
        blockFactory = new MarkdownBlockFactory(repository);
    }

    [Fact]
    public void RewriteIntroduction_ReplacesFirstParagraphAndKeepsOrder()
    {
        var doc = CreateAdventureDocument();
        var editor = new DocumentEditor();

        var newBlock = blockFactory.CreateBlock(
            "p1_v2",
            "Thunder crashed overhead as the rain lashed against the windowpane.");

        var op = new EditOperation
        {
            Action = EditActionType.Replace,
            TargetBlockId = "p1",
            NewBlock = newBlock
        };

        editor.ApplyEdits(doc, new[] { op });

        var p1 = doc.Blocks.First(b => b.Id == "p1_v2");
        Assert.Equal("Thunder crashed overhead as the rain lashed against the windowpane.", p1.PlainText);
        Assert.Equal(1, doc.Blocks.IndexOf(p1));
    }

    [Fact]
    public void InsertExplanationBeforeCode_PlacesParagraphBeforeTarget()
    {
        var doc = CreateAdventureDocument();
        var editor = new DocumentEditor();

        var explanation = blockFactory.CreateBlock("expl1", "Here is the greeting script:");

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
    public void RemoveDraftNote_RemovesQuoteBlock()
    {
        var doc = CreateAdventureDocument();
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
    public void ReplaceParagraphWithTwo_NewBlocksStaySequential()
    {
        var doc = CreateAdventureDocument();
        var editor = new DocumentEditor();

        var first = blockFactory.CreateBlock("p2_a", "The map was old and crumbling.");
        var second = blockFactory.CreateBlock("p2_b", "The room was silent.");

        var ops = new List<EditOperation>
        {
            new() { Action = EditActionType.Replace, TargetBlockId = "p2", NewBlock = first },
            new() { Action = EditActionType.InsertAfter, TargetBlockId = "p2_a", NewBlock = second }
        };

        editor.ApplyEdits(doc, ops);

        Assert.Contains(doc.Blocks, b => b.Id == "p2_a");
        Assert.Contains(doc.Blocks, b => b.Id == "p2_b");
        Assert.DoesNotContain(doc.Blocks, b => b.Id == "p2");

        var indexA = doc.Blocks.FindIndex(b => b.Id == "p2_a");
        var indexB = doc.Blocks.FindIndex(b => b.Id == "p2_b");

        Assert.Equal(indexA + 1, indexB);
    }

    [Theory]
    [MemberData(nameof(AddExplanationBeforeCodeCases))]
    public void AddExplanationBeforeCode_ProducesExpectedMarkdown(MarkdownEditCase scenario)
    {
        var document = repository.LoadFromMarkdown(scenario.InputMarkdown);
        var editor = new DocumentEditor();
        var codeBlock = document.Blocks.First(b => b.Type == BlockType.Code);
        var explanation = blockFactory.CreateBlock("explanation", ExplanationText);

        var op = new EditOperation
        {
            Action = EditActionType.InsertBefore,
            TargetBlockId = codeBlock.Id,
            NewBlock = explanation
        };

        editor.ApplyEdits(document, new[] { op });

        var actual = repository.SaveToMarkdown(document);
        var expected = repository.SaveToMarkdown(repository.LoadFromMarkdown(scenario.ExpectedMarkdown));
        Assert.Equal(expected, actual);
    }

    public static TheoryData<MarkdownEditCase> AddExplanationBeforeCodeCases => new()
    {
        new MarkdownEditCase(
            "CodeInMiddle",
            """
            # Minimal API demo

            The paragraph before code.

            ```csharp
            var builder = WebApplication.CreateBuilder(args);
            var app = builder.Build();
            app.MapGet("/ping", () => "pong");
            app.Run();
            ```

            After code we mention health checks.
            """,
            """
            # Minimal API demo

            The paragraph before code.

            Here is what the snippet does before we dive into the code.

            ```
            var builder = WebApplication.CreateBuilder(args);
            var app = builder.Build();
            app.MapGet("/ping", () => "pong");
            app.Run();
            ```

            After code we mention health checks.
            """
        ),
        new MarkdownEditCase(
            "CodeAtStart",
            """
            ```csharp
            Console.WriteLine("Hello, world!");
            ```

            This sample prints to the console.
            """,
            """
            Here is what the snippet does before we dive into the code.

            ```
            Console.WriteLine("Hello, world!");
            ```

            This sample prints to the console.
            """
        ),
        new MarkdownEditCase(
            "CodeAtEnd",
            """
            ## Setup

            Configure services before building the app.

            ```csharp
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddSwaggerGen();
            ```
            """,
            """
            ## Setup

            Configure services before building the app.

            Here is what the snippet does before we dive into the code.

            ```
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddSwaggerGen();
            ```
            """
        )
    };

    private Document CreateAdventureDocument()
    {
        return new Document
        {
            Blocks =
            [
                blockFactory.CreateBlock("title", "# The Lost City"),
                blockFactory.CreateBlock("p1", "It was a dark and stormy night."),
                blockFactory.CreateBlock("p2", "The detective looked at the map."),
                blockFactory.CreateBlock("code1", "```\nprint('Hello')\n```"),
                blockFactory.CreateBlock("note1", "> TODO: Fix this chapter")
            ]
        };
    }

    private sealed class MarkdownBlockFactory
    {
        private readonly MarkdownDocumentRepository repository;

        public MarkdownBlockFactory(MarkdownDocumentRepository repository)
        {
            this.repository = repository;
        }

        public Block CreateBlock(string blockId, string markdown, string? parentId = null)
        {
            var document = repository.LoadFromMarkdown(markdown);
            if (document.Blocks.Count == 0)
            {
                throw new InvalidOperationException($"No blocks were parsed for '{blockId}'.");
            }

            var parsed = document.Blocks[0];
            if (document.Blocks.Count > 1)
            {
                var combinedPlainText = string.Join("\n", document.Blocks
                    .Select(b => b.PlainText)
                    .Where(text => !string.IsNullOrWhiteSpace(text)));

                if (!string.IsNullOrWhiteSpace(combinedPlainText))
                {
                    parsed.PlainText = combinedPlainText;
                }
            }

            return new Block
            {
                Id = blockId,
                Type = parsed.Type,
                Level = parsed.Level,
                Markdown = parsed.Markdown,
                PlainText = parsed.PlainText,
                ParentId = parentId
            };
        }
    }

}
