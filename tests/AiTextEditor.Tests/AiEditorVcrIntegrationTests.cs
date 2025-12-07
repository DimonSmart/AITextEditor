using AiTextEditor.Lib.Model;
using AiTextEditor.Lib.Services;
using AiTextEditor.Tests.Infrastructure;
using Xunit;

namespace AiTextEditor.Tests;

public class AiEditorVcrIntegrationTests
{
    [Fact]
    public async Task PlanAsync_WithRealLlm_VcrCachesResponses()
    {
        const string cassetteName = "plan";
        using var context = LlmTestHelper.CreateClient(cassetteName, cassetteSubdirectory: "ai-editor");
        var cassetteDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "cassettes", "ai-editor", cassetteName));

        Directory.CreateDirectory(cassetteDir);
        var filesBefore = Directory.GetFiles(cassetteDir).Length;

        var document = new Document
        {
            Blocks =
            [
                new Block { Id = "h_intro", Type = BlockType.Heading, Level = 1, Numbering = "1", Markdown = "# Intro", PlainText = "Intro", StructuralPath = "1" },
                new Block { Id = "p_intro", Type = BlockType.Paragraph, Markdown = "Old intro", PlainText = "Old intro", StructuralPath = "1.p1" }
            ],
            LinearDocument = new LinearDocument
            {
                Items =
                [
                    new LinearItem { Index = 0, Type = LinearItemType.Heading, Level = 1, Markdown = "# Intro", Text = "Intro", Pointer = new LinearPointer(0, new SemanticPointer(new[] { 1 }, null)) },
                    new LinearItem { Index = 1, Type = LinearItemType.Paragraph, Markdown = "Old intro", Text = "Old intro", Pointer = new LinearPointer(1, new SemanticPointer(new[] { 1 }, 1)) }
                ]
            }
        };

        var request = "Rewrite the intro paragraph to be punchy.";

        var targetSetService = new InMemoryTargetSetService();
        var planner = new AiCommandPlanner(
            new DocumentIndexBuilder(),
            new VectorIndexingService(new SimpleEmbeddingGenerator(), new InMemoryVectorIndex()),
            new IntentParser(context.LlmClient),
            targetSetService);
        var generator = new EditOperationGenerator(targetSetService, new FunctionCallingLlmEditor(context.LlmClient));

        var plan = await planner.PlanAsync(document, request);
        Assert.True(plan.Success);
        var ops1 = await generator.GenerateAsync(document, plan.TargetSet!.Id, plan.Intent!, request);
        var filesAfterFirst = Directory.GetFiles(cassetteDir).Length;

        var plan2 = await planner.PlanAsync(document, request);
        Assert.True(plan2.Success);
        var ops2 = await generator.GenerateAsync(document, plan2.TargetSet!.Id, plan2.Intent!, request);
        var filesAfterSecond = Directory.GetFiles(cassetteDir).Length;

        Assert.True(filesAfterFirst >= filesBefore, "Cassette files should be created after first run.");
        Assert.Equal(filesAfterFirst, filesAfterSecond); // second call should hit VCR, not add new files
        Assert.NotNull(ops1);
        Assert.NotNull(ops2);
    }
}
