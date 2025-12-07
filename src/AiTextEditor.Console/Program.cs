using AiTextEditor.Lib.Interfaces;
using AiTextEditor.Lib.Model;
using AiTextEditor.Lib.Services;

Console.WriteLine("--- AI Text Editor Prototype ---");

// 1. Setup Services
IDocumentRepository repository = new MarkdownDocumentRepository();
var ollamaEndpoint = Environment.GetEnvironmentVariable("OLLAMA_ENDPOINT") ?? "http://localhost:11434";
var ollamaModel = Environment.GetEnvironmentVariable("OLLAMA_MODEL") ?? "qwen3:latest";

ILlmClient llmClient = SemanticKernelLlmClient.CreateOllamaClient(
    modelId: ollamaModel,
    endpoint: new Uri(ollamaEndpoint));

ILlmEditor llmEditor = new FunctionCallingLlmEditor(llmClient);
ITargetSetService targetSetService = new InMemoryTargetSetService();
IDocumentEditor docEditor = new DocumentEditor();
var indexBuilder = new DocumentIndexBuilder();
IEmbeddingGenerator embeddingGenerator = new SimpleEmbeddingGenerator();
IVectorIndex vectorIndex = new InMemoryVectorIndex();
var vectorIndexing = new VectorIndexingService(embeddingGenerator, vectorIndex);
var planner = new AiCommandPlanner(indexBuilder, vectorIndexing, targetSetService);
var editGenerator = new EditOperationGenerator(targetSetService, llmEditor);

string inputPath = "sample.md";
string outputPath = "sample_edited.md";

if (!File.Exists(inputPath))
{
    Console.WriteLine($"Error: {inputPath} not found.");
    return;
}

// 2. Load Document
Console.WriteLine($"Loading {inputPath}...");
Document document = repository.LoadFromMarkdownFile(inputPath);
Console.WriteLine($"Loaded {document.Blocks.Count} blocks.");

// 3. Get user command (demo)
string userCommand = "Добавь TODO во вторую главу: проверить, что все примеры компилируются.";
Console.WriteLine($"User command: {userCommand}");

// 4. Plan edits via AiCommandPlanner (indexes + raw command)
var plan = await planner.PlanAsync(document, userCommand);
Console.WriteLine(plan.Success
    ? $"Target set {plan.TargetSet!.Id} with {plan.TargetSet!.Targets.Count} items created."
    : "Planning failed.");

if (plan.Success)
{
    Console.WriteLine("Generating operations...");
    var operations = await editGenerator.GenerateAsync(document, plan.TargetSet!.Id, userCommand);
    Console.WriteLine($"Planned {operations.Count} operations.");
    foreach (var op in operations)
    {
        Console.WriteLine($"Op: {op.Action} on {op.TargetBlockId}");
    }

    Console.WriteLine("Applying edits...");
    docEditor.ApplyEdits(document, operations);

    Console.WriteLine($"Saving to {outputPath}...");
    repository.SaveToMarkdownFile(document, outputPath);
    Console.WriteLine("Done.");

    Console.WriteLine("\n--- Result Preview ---");
    var savedText = File.ReadAllText(outputPath);
    Console.WriteLine(savedText);
}
