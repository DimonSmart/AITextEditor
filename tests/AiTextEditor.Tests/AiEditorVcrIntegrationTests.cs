using System.Net;
using System.Text;
using AiTextEditor.Lib.Model;
using AiTextEditor.Lib.Services;
using AiTextEditor.Tests.Infrastructure;
using Xunit;

namespace AiTextEditor.Tests;

public class AiEditorVcrIntegrationTests
{
    [Fact]
    public async Task PlanAsync_WithOllamaVcr_ReplacesParagraphAndCachesResponse()
    {
        // Arrange: document with a heading and one paragraph
        var document = new Document
        {
            Blocks =
            [
                new Block { Id = "h_intro", Type = BlockType.Heading, Level = 1, Markdown = "# Intro", PlainText = "Intro" },
                new Block { Id = "p_intro", Type = BlockType.Paragraph, Markdown = "Old intro", PlainText = "Old intro" }
            ]
        };

        var request = "В главе Intro замени первый параграф на \"Новый текст\".";

        var cassetteDir = Path.Combine(Path.GetTempPath(), "ai-text-editor-vcr", Guid.NewGuid().ToString("N"));
        int liveCallCount = 0;

        var innerHandler = new Func<HttpRequestMessage, HttpResponseMessage>(_ =>
        {
            Interlocked.Increment(ref liveCallCount);
            var body = """
{"model":"qwen3:latest","created_at":"2024-01-01T00:00:00Z","message":{"role":"assistant","content":"[{\"action\":\"replace\",\"targetBlockId\":\"p_intro\",\"blockType\":\"paragraph\",\"markdown\":\"Новый текст\",\"plainText\":\"Новый текст\"}]"},"done":false}
{"model":"qwen3:latest","created_at":"2024-01-01T00:00:00Z","message":{"role":"assistant","content":""},"done":true,"done_reason":"stop","total_duration":0,"load_duration":0,"prompt_eval_count":0,"prompt_eval_duration":0,"eval_count":0,"eval_duration":0}
""";

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        });

        using var vcr = new HttpClientVcr(cassetteDir, new LambdaHandler(innerHandler));
        using var httpClient = new HttpClient(vcr)
        {
            BaseAddress = new Uri("http://localhost:11434")
        };

        var llmClient = SemanticKernelLlmClient.CreateOllamaClient(
            modelId: "qwen3:latest",
            httpClient: httpClient);

        var planner = new AiCommandPlanner(
            new ChunkBuilder(),
            new InMemoryVectorStore(),
            new FunctionCallingLlmEditor(llmClient),
            maxTokensPerChunk: 200);

        // Act: two identical calls should hit live once and replay once
        var ops1 = await planner.PlanAsync(document, request);
        var ops2 = await planner.PlanAsync(document, request);

        // Assert
        Assert.Equal(1, liveCallCount); // second call served from cassette

        var op = Assert.Single(ops1);
        Assert.Equal(EditActionType.Replace, op.Action);
        Assert.Equal("p_intro", op.TargetBlockId);
        Assert.Equal("Новый текст", op.NewBlock?.PlainText);

        // second call returns the same cached operation
        var op2 = Assert.Single(ops2);
        Assert.Equal("Новый текст", op2.NewBlock?.PlainText);
    }

    private sealed class LambdaHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> handler;

        public LambdaHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            this.handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(handler(request));
        }
    }
}
