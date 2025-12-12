using AiTextEditor.Lib.Services;
using System.Text;

var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
var bookPath = Path.Combine(repoRoot, "tests", "Samples", "neznayka_sample.md");
if (!File.Exists(bookPath))
{
    Console.Error.WriteLine($"Book not found: {bookPath}");
    return 1;
}

var outputPath = Path.Combine(Path.GetDirectoryName(bookPath)!, "neznayka_sample_tagged.md");

var repo = new MarkdownDocumentRepository();
var markdown = await File.ReadAllTextAsync(bookPath, Encoding.UTF8);
var document = repo.LoadFromMarkdown(markdown);

var sb = new StringBuilder();
foreach (var item in document.Items)
{
    var pointerLabel = item.Pointer.Label ?? $"p{item.Index}";
    sb.AppendLine($"{item.Pointer.Id}:{pointerLabel}");
    sb.AppendLine(item.Markdown);
    sb.AppendLine();
}

await File.WriteAllTextAsync(outputPath, sb.ToString(), Encoding.UTF8);
Console.WriteLine($"Tagged book written to {outputPath}");
return 0;
