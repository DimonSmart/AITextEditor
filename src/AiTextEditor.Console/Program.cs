using AiTextEditor.Lib.Interfaces;
using AiTextEditor.Lib.Model;
using AiTextEditor.Lib.Services;

Console.WriteLine("--- AI Text Editor Prototype ---");

// 1. Setup Services
IDocumentRepository repository = new MarkdownDocumentRepository();
IChunkBuilder chunkBuilder = new ChunkBuilder();
ILlmEditor llmEditor = new MockLlmEditor();
IDocumentEditor docEditor = new DocumentEditor();

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

// 3. Build Chunks
Console.WriteLine("Building chunks...");
var chunks = chunkBuilder.BuildChunks(document, maxTokensApprox: 50); // Small maxTokens to see chunks
foreach (var chunk in chunks)
{
    Console.WriteLine($"Chunk {chunk.Id.Substring(0, 8)}: {chunk.BlockIds.Count} blocks. Path: {chunk.HeadingPath}");
}

// 4. Select a block to edit (First paragraph after first heading)
// In sample.md:
// Block 0: Heading (# Chapter 1...)
// Block 1: Paragraph (This is the first paragraph...)
var targetBlock = document.Blocks.FirstOrDefault(b => b.Type == BlockType.Paragraph);

if (targetBlock != null)
{
    Console.WriteLine($"\nSelected target block: {targetBlock.Id} - {targetBlock.PlainText.Substring(0, Math.Min(20, targetBlock.PlainText.Length))}...");

    // 5. Call LLM Editor
    Console.WriteLine("Calling LLM Editor...");
    var context = new List<Block> { targetBlock }; // Minimal context
    string userText = "This is a NEW paragraph inserted by the AI.";
    string instruction = "Insert new paragraph after the selected one.";

    var operations = await llmEditor.GetEditOperationsAsync(context, userText, instruction);

    Console.WriteLine($"Received {operations.Count} operations.");
    foreach (var op in operations)
    {
        Console.WriteLine($"Op: {op.Action} on {op.TargetBlockId}");
    }

    // 6. Apply Edits
    Console.WriteLine("Applying edits...");
    docEditor.ApplyEdits(document, operations);

    // 7. Save Result
    Console.WriteLine($"Saving to {outputPath}...");
    repository.SaveToMarkdownFile(document, outputPath);
    Console.WriteLine("Done.");

    // Show result preview
    Console.WriteLine("\n--- Result Preview ---");
    var savedText = File.ReadAllText(outputPath);
    Console.WriteLine(savedText);
}
else
{
    Console.WriteLine("No paragraph found to edit.");
}
