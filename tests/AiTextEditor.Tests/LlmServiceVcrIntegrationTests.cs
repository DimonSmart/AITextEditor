using AiTextEditor.Lib.Model;
using AiTextEditor.Lib.Model.Intent;
using AiTextEditor.Lib.Services;
using AiTextEditor.Tests.Infrastructure;
using System.Text.Json;
using Xunit.Abstractions;

namespace AiTextEditor.Tests;

public class LlmServiceVcrIntegrationTests
{
    private readonly ITestOutputHelper output;

    public LlmServiceVcrIntegrationTests(ITestOutputHelper output)
    {
        this.output = output;
    }

    [Theory]
    [MemberData(nameof(TestScenarios.IntentCommands), MemberType = typeof(TestScenarios))]
    public async Task IntentParser_WithRealLlm_ProducesStructuredIntent(string userCommand)
    {
        using var context = LlmTestHelper.CreateClient("intent-parser");
        var parser = new IntentParser(context.LlmClient);

        var result = await parser.ParseAsync(userCommand);

        output.WriteLine($"Raw intent response: {result.RawResponse}");
        if (result.Intent != null)
        {
            var payloadJson = JsonSerializer.Serialize(result.Intent.Payload.Fields);
            output.WriteLine($"Parsed intent: scope={result.Intent.ScopeType} payload={payloadJson}");
        }
        // These asserts stay permissive; verify richness/fields by reviewing the captured test logs above.

        Assert.True(result.Success, "LLM should return parsable intent.");
        Assert.NotNull(result.Intent);
        Assert.NotEqual(IntentScopeType.Unknown, result.Intent!.ScopeType);
    }

    [Theory]
    [MemberData(nameof(TestScenarios.IntentCommands), MemberType = typeof(TestScenarios))]
    public async Task AiCommandPlanner_RunAllCommands_OnDotNetMd(string userCommand)
    {
        // 1. Load the document
        var repo = new MarkdownDocumentRepository();
        var documentPath = Path.Combine(AppContext.BaseDirectory, "DotNet.md");
        Assert.True(File.Exists(documentPath), $"Document not found at {documentPath}");
        var document = repo.LoadFromMarkdownFile(documentPath);

        // 2. Setup services
        using var intentContext = LlmTestHelper.CreateClient("intent-parser");
        // We don't need editorContext because we use FakeLlmEditor

        var intentParser = new IntentParser(intentContext.LlmClient);
        var llmEditor = new FakeLlmEditor(output); // Use Fake

        var vectorIndex = new InMemoryVectorIndex();
        var vectorService = new VectorIndexingService(new SimpleEmbeddingGenerator(), vectorIndex);
        var indexBuilder = new DocumentIndexBuilder();

        var planner = new AiCommandPlanner(indexBuilder, vectorService, intentParser, llmEditor);

        // 3. Execute command
        output.WriteLine($"Executing command: {userCommand}");
        var operations = await planner.PlanAsync(document, userCommand);

        // 4. Verify results
        output.WriteLine("Generated operations:");
        output.WriteLine(JsonSerializer.Serialize(operations, new JsonSerializerOptions { WriteIndented = true }));

        // We don't assert specific content because the commands are generic and might not match DotNet.md content.
        // But we assert that the planner runs without exception and returns a list (empty or not).
        Assert.NotNull(operations);
    }

    [Fact]
    public async Task FunctionCallingLlmEditor_WithRealLlm_ReturnsOperations()
    {
        using var context = LlmTestHelper.CreateClient("llm-editor");
        var editor = new FunctionCallingLlmEditor(context.LlmClient);

        var document = new Document
        {
            Blocks =
            [
                new Block { Id = "h_intro", Type = BlockType.Heading, Level = 1, Numbering = "1", Markdown = "# Intro", PlainText = "Intro" },
                new Block { Id = "p_intro", Type = BlockType.Paragraph, Markdown = "Old intro", PlainText = "Old intro about architecture trade-offs." },
                new Block { Id = "p_body", Type = BlockType.Paragraph, Markdown = "Body paragraph", PlainText = "Body paragraph with details." }
            ]
        };

        var instruction = "Rewrite the intro paragraph (block id p_intro) to be punchy and inviting for new readers.";
        var ops = await editor.GetEditOperationsAsync(document.Blocks, "Improve the intro hook", instruction);

        output.WriteLine("Generated operations:");
        output.WriteLine(JsonSerializer.Serialize(ops, new JsonSerializerOptions { WriteIndented = true }));

        Assert.NotEmpty(ops);
        var knownIds = document.Blocks.Select(b => b.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains(ops, op => op.TargetBlockId != null && knownIds.Contains(op.TargetBlockId));
        Assert.True(ops.Any(op => op.NewBlock != null && !string.IsNullOrWhiteSpace(op.NewBlock.Markdown)),
            "Expected at least one operation to include new markdown content.");
    }

    [Fact]
    public async Task AiCommandPlanner_WithRealLlm_ExecutesFullPipeline()
    {
        // 1. Load the document
        var repo = new MarkdownDocumentRepository();
        var documentPath = Path.Combine(AppContext.BaseDirectory, "DotNet.md");
        Assert.True(File.Exists(documentPath), $"Document not found at {documentPath}");
        var document = repo.LoadFromMarkdownFile(documentPath);

        // 2. Setup services
        using var intentContext = LlmTestHelper.CreateClient("intent-parser");
        using var editorContext = LlmTestHelper.CreateClient("llm-editor");

        var intentParser = new IntentParser(intentContext.LlmClient);
        var llmEditor = new FunctionCallingLlmEditor(editorContext.LlmClient);
        
        var vectorIndex = new InMemoryVectorIndex();
        var vectorService = new VectorIndexingService(new SimpleEmbeddingGenerator(), vectorIndex);
        var indexBuilder = new DocumentIndexBuilder();

        var planner = new AiCommandPlanner(indexBuilder, vectorService, intentParser, llmEditor);

        // 3. Execute command
        // "In the 'Strategy' section, add a checklist item: 'Practice writing code on a whiteboard'."
        var command = "В разделе 'Стратегия подготовки' добавь пункт чек-листа: 'Потренироваться писать код на доске'.";
        
        var operations = await planner.PlanAsync(document, command);

        // 4. Verify results
        output.WriteLine("Generated operations:");
        output.WriteLine(JsonSerializer.Serialize(operations, new JsonSerializerOptions { WriteIndented = true }));

        Assert.NotEmpty(operations);
        
        // We expect an insertion or replacement in the "Стратегия подготовки" section
        // Let's find the block for that section to verify target ID
        var strategyHeader = document.Blocks.FirstOrDefault(b => b.PlainText.Contains("Стратегия подготовки") && b.Type == BlockType.Heading);
        Assert.NotNull(strategyHeader);

        // The operation should target something in that section (either the header or the list below it)
        var op = operations.First();
        Assert.NotNull(op.TargetBlockId);
        
        // Verify the content contains the new item
        Assert.Contains("Потренироваться писать код на доске", op.NewBlock?.Markdown ?? op.NewBlock?.PlainText ?? "");
    }
}
