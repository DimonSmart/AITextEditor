using AiTextEditor.Lib.Model;
using AiTextEditor.Lib.Services;

Console.WriteLine("--- MCP Linear Document Demo ---");

var server = new EditorSession();
var inputPath = args.Length > 0 ? args[0] : "sample.md";
var outputPath = args.Length > 1 ? args[1] : "sample_edited.md";

if (!File.Exists(inputPath))
{
    Console.WriteLine($"Error: {inputPath} not found.");
    return;
}

var markdown = File.ReadAllText(inputPath);
var document = server.LoadDefaultDocument(markdown);
Console.WriteLine($"Loaded document {document.Id} with {document.Items.Count} items.");

var targetSet = server.CreateTargetSet(Enumerable.Range(0, Math.Min(3, document.Items.Count)), "demo", "first-items");
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

var updated = operations.Count > 0 ? server.ApplyOperations(operations) : document;
File.WriteAllText(outputPath, string.Join("\n\n", updated.Items.Select(item => item.Markdown)));
Console.WriteLine($"Saved updated document to {outputPath}.");
