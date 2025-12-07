using AiTextEditor.Lib.Interfaces;
using AiTextEditor.Lib.Model;
using AiTextEditor.Lib.Model.Indexing;
using AiTextEditor.Lib.Services;
using AiTextEditor.Tests.Fakes;
using Xunit;
using System.Linq;
using AiTextEditor.Lib.Model.Intent;

namespace AiTextEditor.Tests;

public class AiCommandPlannerTests
{
    [Fact]
    public async Task PlanAsync_SelectsStructuralScopeBlocks()
    {
        var document = CreateDocument();

        var intentLlm = new InspectableLlmClient(_ =>
        {
            return """
            {
              "scopeType": "Structural",
              "scopeDescriptor": { "chapterNumber": 2 },
              "payload": { "todoText": "Check examples" }
            }
            """;
        });

        var opsLlm = new InspectableLlmClient(prompt =>
        {
            Assert.Contains("2.p1", prompt);
            Assert.DoesNotContain("1.p1", prompt);

            return """
            [
              {
                "action": "insert_after",
                "targetBlockId": "p2",
                "blockType": "paragraph",
                "markdown": "TODO: check examples.",
                "plainText": "TODO: check examples."
              }
            ]
            """;
        });

        var targetSetService = new InMemoryTargetSetService();
        var planner = CreatePlanner(intentLlm, targetSetService);
        var generator = new EditOperationGenerator(targetSetService, new FunctionCallingLlmEditor(opsLlm));

        var plan = await planner.PlanAsync(document, "Добавь TODO во вторую главу");
        Assert.True(plan.Success);
        Assert.Equal("2", plan.TargetSet!.Targets[0].Pointer.SemanticNumber);

        var ops = await generator.GenerateAsync(document, plan.TargetSet.Id, plan.Intent!, "Добавь TODO во вторую главу");

        var op = Assert.Single(ops);
        Assert.Equal(EditActionType.InsertAfter, op.Action);
        Assert.Equal("p2", op.TargetBlockId);
        Assert.Equal("TODO: check examples.", op.NewBlock?.PlainText);
    }

    [Fact]
    public async Task PlanAsync_UsesSemanticScopeForTargets()
    {
        var document = CreateSemanticDocument();

        var intentLlm = new InspectableLlmClient(_ =>
        {
            return """
            {
              "scopeType": "SemanticLocal",
              "scopeDescriptor": { "semanticQuery": "monolith microservices" },
              "payload": { "style": "simpler" }
            }
            """;
        });

        var opsLlm = new InspectableLlmClient(prompt =>
        {
            Assert.Contains("1.p1", prompt);

            return """
            [
              {
                "action": "replace",
                "targetBlockId": "p1",
                "blockType": "paragraph",
                "markdown": "Simpler text",
                "plainText": "Simpler text"
              }
            ]
            """;
        });

        var targetSetService = new InMemoryTargetSetService();
        var planner = CreatePlanner(intentLlm, targetSetService, new KeywordVectorIndex());
        var generator = new EditOperationGenerator(targetSetService, new FunctionCallingLlmEditor(opsLlm));

        var plan = await planner.PlanAsync(document, "Найди монолит и микросервисы и упростись");
        Assert.True(plan.Success);
        Assert.Contains(plan.TargetSet!.Targets, t => t.Pointer.SemanticNumber == "1.p1");

        var ops = await generator.GenerateAsync(document, plan.TargetSet.Id, plan.Intent!, "Найди монолит и микросервисы и упростись");

        var op = Assert.Single(ops);
        Assert.Equal(EditActionType.Replace, op.Action);
        Assert.Equal("p1", op.TargetBlockId);
        Assert.Equal("Simpler text", op.NewBlock?.PlainText);
    }

    private static AiCommandPlanner CreatePlanner(ILlmClient intentLlm, ITargetSetService targetSetService, IVectorIndex? vectorIndex = null)
    {
        var vector = vectorIndex ?? new InMemoryVectorIndex();
        var vectorService = new VectorIndexingService(new SimpleEmbeddingGenerator(), vector);
        var intentParser = new IntentParser(intentLlm);
        return new AiCommandPlanner(
            new DocumentIndexBuilder(),
            vectorService,
            intentParser,
            targetSetService);
    }

    private static Document CreateDocument()
    {
        return new Document
        {
            Blocks =
            [
                new Block { Id = "h1", Type = BlockType.Heading, Level = 1, Numbering = "1", Markdown = "# Chapter 1", PlainText = "Chapter 1", StructuralPath = "1" },
                new Block { Id = "p1", Type = BlockType.Paragraph, Markdown = "Intro text", PlainText = "Intro text", StructuralPath = "1.p1" },
                new Block { Id = "h2", Type = BlockType.Heading, Level = 1, Numbering = "2", Markdown = "# Chapter 2", PlainText = "Chapter 2", StructuralPath = "2" },
                new Block { Id = "p2", Type = BlockType.Paragraph, Markdown = "Second chapter text", PlainText = "Second chapter text", StructuralPath = "2.p1" },
                new Block { Id = "p3", Type = BlockType.Paragraph, Markdown = "More text", PlainText = "More text", StructuralPath = "2.p2" },
            ],
            LinearDocument = new LinearDocument
            {
                Items =
                [
                    new LinearItem { Index = 0, Type = LinearItemType.Heading, Level = 1, Markdown = "# Chapter 1", Text = "Chapter 1", Pointer = new LinearPointer(0, new SemanticPointer(new[] { 1 }, null)) },
                    new LinearItem { Index = 1, Type = LinearItemType.Paragraph, Markdown = "Intro text", Text = "Intro text", Pointer = new LinearPointer(1, new SemanticPointer(new[] { 1 }, 1)) },
                    new LinearItem { Index = 2, Type = LinearItemType.Heading, Level = 1, Markdown = "# Chapter 2", Text = "Chapter 2", Pointer = new LinearPointer(2, new SemanticPointer(new[] { 2 }, null)) },
                    new LinearItem { Index = 3, Type = LinearItemType.Paragraph, Markdown = "Second chapter text", Text = "Second chapter text", Pointer = new LinearPointer(3, new SemanticPointer(new[] { 2 }, 1)) },
                    new LinearItem { Index = 4, Type = LinearItemType.Paragraph, Markdown = "More text", Text = "More text", Pointer = new LinearPointer(4, new SemanticPointer(new[] { 2 }, 2)) }
                ]
            }
        };
    }

    private static Document CreateSemanticDocument()
    {
        return new Document
        {
            Blocks =
            [
                new Block { Id = "h_ch1", Type = BlockType.Heading, Level = 1, Numbering = "1", Markdown = "# Chapter 1", PlainText = "Chapter 1", StructuralPath = "1" },
                new Block { Id = "p1", Type = BlockType.Paragraph, Markdown = "Intro paragraph about monolith vs microservices", PlainText = "Intro paragraph about monolith vs microservices", StructuralPath = "1.p1" },
                new Block { Id = "p2", Type = BlockType.Paragraph, Markdown = "Another paragraph", PlainText = "Another paragraph", StructuralPath = "1.p2" }
            ],
            LinearDocument = new LinearDocument
            {
                Items =
                [
                    new LinearItem { Index = 0, Type = LinearItemType.Heading, Level = 1, Markdown = "# Chapter 1", Text = "Chapter 1", Pointer = new LinearPointer(0, new SemanticPointer(new[] { 1 }, null)) },
                    new LinearItem { Index = 1, Type = LinearItemType.Paragraph, Markdown = "Intro paragraph about monolith vs microservices", Text = "Intro paragraph about monolith vs microservices", Pointer = new LinearPointer(1, new SemanticPointer(new[] { 1 }, 1)) },
                    new LinearItem { Index = 2, Type = LinearItemType.Paragraph, Markdown = "Another paragraph", Text = "Another paragraph", Pointer = new LinearPointer(2, new SemanticPointer(new[] { 1 }, 2)) }
                ]
            }
        };
    }

    private sealed class KeywordVectorIndex : IVectorIndex
    {
        private readonly Dictionary<string, List<VectorRecord>> store = new(StringComparer.OrdinalIgnoreCase);

        public Task IndexAsync(string documentId, IEnumerable<VectorRecord> records, CancellationToken ct = default)
        {
            store[documentId] = records.ToList();
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<VectorRecord>> QueryAsync(string documentId, float[] queryEmbedding, int maxResults = 5, CancellationToken ct = default)
        {
            if (!store.TryGetValue(documentId, out var records))
            {
                return Task.FromResult<IReadOnlyList<VectorRecord>>(Array.Empty<VectorRecord>());
            }

            var preferred = records.Where(r =>
                r.Text.Contains("monolith", StringComparison.OrdinalIgnoreCase) ||
                r.Text.Contains("microservices", StringComparison.OrdinalIgnoreCase))
                .Take(maxResults)
                .ToList();

            if (preferred.Count == 0)
            {
                preferred = records.Take(maxResults).ToList();
            }

            return Task.FromResult<IReadOnlyList<VectorRecord>>(preferred);
        }
    }
}
