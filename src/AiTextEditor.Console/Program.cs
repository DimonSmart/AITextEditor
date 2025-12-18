using AiTextEditor.Lib.Model;
using AiTextEditor.Lib.Services;

Console.WriteLine("--- MCP Linear Document Demo ---");

var server = new McpServer();
var inputPath = args.Length > 0 ? args[0] : "sample.md";
var outputPath = args.Length > 1 ? args[1] : "sample_edited.md";
var scenario = args.Length > 2 ? args[2] : "sample";

if (!File.Exists(inputPath))
{
    Console.WriteLine($"Error: {inputPath} not found.");
    return;
}

var markdown = File.ReadAllText(inputPath);
var document = server.LoadDocument(markdown, scenario);
Console.WriteLine($"Loaded document {document.Id} with {document.Items.Count} items.");

var targetSet = server.CreateTargetSet(document.Id, Enumerable.Range(0, Math.Min(3, document.Items.Count)), scenario, "first-items");
Console.WriteLine($"Target set {targetSet.Id} created with {targetSet.Targets.Count} targets.");

var operations = new List<LinearEditOperation>();
if (document.Items.Count > 0)
{
    var first = document.Items[0];
    var updatedFirst = first with
    {
        Markdown = first.Markdown + "\n\n<!-- edited by MCP demo -->",
        Text = first.Text + "\n\n[annotated by MCP demo]"
    };

    operations.Add(new LinearEditOperation(LinearEditAction.Replace, first.Pointer, null, new[] { updatedFirst }));
}

var updated = operations.Count > 0 ? server.ApplyOperations(document.Id, operations) : document;
File.WriteAllText(outputPath, string.Join("\n\n", updated.Items.Select(item => item.Markdown)));
Console.WriteLine($"Saved updated document to {outputPath}.");
