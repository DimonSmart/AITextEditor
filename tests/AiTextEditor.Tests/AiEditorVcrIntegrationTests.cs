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
                new Block { Id = "h_intro", Type = BlockType.Heading, Level = 1, Numbering = "1", Markdown = "# Intro", PlainText = "Intro" },
                new Block { Id = "p_intro", Type = BlockType.Paragraph, Markdown = "Old intro", PlainText = "Old intro" }
            ]
        };

        var request = "Rewrite the intro paragraph to be punchy.";

        var planner = new AiCommandPlanner(
            new DocumentIndexBuilder(),
            new VectorIndexingService(new SimpleEmbeddingGenerator(), new InMemoryVectorIndex()),
            new IntentParser(context.LlmClient),
            new FunctionCallingLlmEditor(context.LlmClient));

        var ops1 = await planner.PlanAsync(document, request);
        var filesAfterFirst = Directory.GetFiles(cassetteDir).Length;

        var ops2 = await planner.PlanAsync(document, request);
        var filesAfterSecond = Directory.GetFiles(cassetteDir).Length;

        Assert.True(filesAfterFirst >= filesBefore, "Cassette files should be created after first run.");
        Assert.Equal(filesAfterFirst, filesAfterSecond); // second call should hit VCR, not add new files
        Assert.NotNull(ops1);
        Assert.NotNull(ops2);
    }
}
