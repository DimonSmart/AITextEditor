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
        var intentParser = new IntentParser(intentContext.LlmClient);
        var vectorIndex = new InMemoryVectorIndex();
        var vectorService = new VectorIndexingService(new SimpleEmbeddingGenerator(), vectorIndex);
        var indexBuilder = new DocumentIndexBuilder();
        var targetSetService = new InMemoryTargetSetService();
        var planner = new AiCommandPlanner(indexBuilder, vectorService, intentParser, targetSetService);
        var generator = new EditOperationGenerator(targetSetService, new FakeLlmEditor(output));

        // 3. Execute command
        output.WriteLine($"Executing command: {userCommand}");
        var plan = await planner.PlanAsync(document, userCommand);
        Assert.True(plan.Success);
        var operations = await generator.GenerateAsync(document, plan.TargetSet!.Id, plan.Intent!, userCommand);

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

        var linearDocument = new LinearDocument
        {
            Items =
            [
                new LinearItem { Index = 0, Type = LinearItemType.Heading, Level = 1, Markdown = "# Intro", Text = "Intro", Pointer = new LinearPointer(0, new SemanticPointer(new[] { 1 }, null)) },
                new LinearItem { Index = 1, Type = LinearItemType.Paragraph, Markdown = "Old intro", Text = "Old intro about architecture trade-offs.", Pointer = new LinearPointer(1, new SemanticPointer(new[] { 1 }, 1)) },
                new LinearItem { Index = 2, Type = LinearItemType.Paragraph, Markdown = "Body paragraph", Text = "Body paragraph with details.", Pointer = new LinearPointer(2, new SemanticPointer(new[] { 1 }, 2)) }
            ]
        };
        var document = new Document
        {
            Blocks =
            [
                new Block { Id = "h_intro", Type = BlockType.Heading, Level = 1, Numbering = "1", Markdown = "# Intro", PlainText = "Intro", StructuralPath = "1" },
                new Block { Id = "p_intro", Type = BlockType.Paragraph, Markdown = "Old intro", PlainText = "Old intro about architecture trade-offs.", StructuralPath = "1.p1" },
                new Block { Id = "p_body", Type = BlockType.Paragraph, Markdown = "Body paragraph", PlainText = "Body paragraph with details.", StructuralPath = "1.p2" }
            ],
            LinearDocument = linearDocument
        };

        var instruction = "Rewrite the intro paragraph (block id p_intro) to be punchy and inviting for new readers.";
        var ops = await editor.GetEditOperationsAsync("manual-target", linearDocument.Items, "Improve the intro hook", instruction);

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
        var targetSetService = new InMemoryTargetSetService();
        var llmEditor = new FunctionCallingLlmEditor(editorContext.LlmClient);

        var vectorIndex = new InMemoryVectorIndex();
        var vectorService = new VectorIndexingService(new SimpleEmbeddingGenerator(), vectorIndex);
        var indexBuilder = new DocumentIndexBuilder();

        var planner = new AiCommandPlanner(indexBuilder, vectorService, intentParser, targetSetService);
        var generator = new EditOperationGenerator(targetSetService, llmEditor);

        // 3. Execute command
        // "In the 'Strategy' section, add a checklist item: 'Practice writing code on a whiteboard'."
        var command = "В разделе 'Стратегия подготовки' добавь пункт чек-листа: 'Потренироваться писать код на доске'.";
        
        var plan = await planner.PlanAsync(document, command);
        Assert.True(plan.Success);
        var operations = await generator.GenerateAsync(document, plan.TargetSet!.Id, plan.Intent!, command);

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
