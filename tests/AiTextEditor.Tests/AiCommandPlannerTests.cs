using AiTextEditor.Lib.Interfaces;
using AiTextEditor.Lib.Model;
using AiTextEditor.Lib.Model.Indexing;
using AiTextEditor.Lib.Services;
using AiTextEditor.Tests.Fakes;
using Xunit;
using System.Linq;

namespace AiTextEditor.Tests;

public class AiCommandPlannerTests
{
    [Fact]
    public async Task PlanAsync_SelectsStructuralScopeBlocks()
    {
        var document = new Document
        {
            Blocks =
            [
                new Block { Id = "h1", Type = BlockType.Heading, Level = 1, Numbering = "1", Markdown = "# Chapter 1", PlainText = "Chapter 1" },
                new Block { Id = "p1", Type = BlockType.Paragraph, Markdown = "Intro text", PlainText = "Intro text" },
                new Block { Id = "h2", Type = BlockType.Heading, Level = 1, Numbering = "2", Markdown = "# Chapter 2", PlainText = "Chapter 2" },
                new Block { Id = "p2", Type = BlockType.Paragraph, Markdown = "Second chapter text", PlainText = "Second chapter text" },
                new Block { Id = "p3", Type = BlockType.Paragraph, Markdown = "More text", PlainText = "More text" },
            ]
        };

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
            Assert.Contains("p2", prompt);
            Assert.DoesNotContain("p1", prompt);

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

        var planner = CreatePlanner(intentLlm, opsLlm);

        var ops = await planner.PlanAsync(document, "Добавь TODO во вторую главу");

        var op = Assert.Single(ops);
        Assert.Equal(EditActionType.InsertAfter, op.Action);
        Assert.Equal("p2", op.TargetBlockId);
        Assert.Equal("TODO: check examples.", op.NewBlock?.PlainText);
    }

    [Fact]
    public async Task PlanAsync_UsesSemanticScopeForTargets()
    {
        var document = new Document
        {
            Blocks =
            [
                new Block { Id = "h_ch1", Type = BlockType.Heading, Level = 1, Numbering = "1", Markdown = "# Chapter 1", PlainText = "Chapter 1" },
                new Block { Id = "p1", Type = BlockType.Paragraph, Markdown = "Intro paragraph about monolith vs microservices", PlainText = "Intro paragraph about monolith vs microservices" },
                new Block { Id = "p2", Type = BlockType.Paragraph, Markdown = "Another paragraph", PlainText = "Another paragraph" }
            ]
        };

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
            Assert.Contains("p1", prompt);

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

        var planner = CreatePlanner(intentLlm, opsLlm, new KeywordVectorIndex());

        var ops = await planner.PlanAsync(document, "Найди монолит и микросервисы и упростись");

        var op = Assert.Single(ops);
        Assert.Equal(EditActionType.Replace, op.Action);
        Assert.Equal("p1", op.TargetBlockId);
        Assert.Equal("Simpler text", op.NewBlock?.PlainText);
    }

    private static AiCommandPlanner CreatePlanner(ILlmClient intentLlm, ILlmClient opsLlm, IVectorIndex? vectorIndex = null)
    {
        var vector = vectorIndex ?? new InMemoryVectorIndex();
        var vectorService = new VectorIndexingService(new SimpleEmbeddingGenerator(), vector);
        var intentParser = new IntentParser(intentLlm);
        var llmEditor = new FunctionCallingLlmEditor(opsLlm);

        return new AiCommandPlanner(
            new DocumentIndexBuilder(),
            vectorService,
            intentParser,
            llmEditor);
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
