#r "src/AiTextEditor.Lib/bin/Debug/net9.0/AiTextEditor.Lib.dll"
using System;
using System.IO;
using AiTextEditor.Lib.Services;

var repo = new MarkdownDocumentRepository();
var md = File.ReadAllText("tests/Samples/neznayka_sample.md");
var doc = repo.LoadFromMarkdown(md);
foreach (var item in doc.Items.Take(30))
{
    var text = item.Text ?? string.Empty;
    var clip = text.Length > 40 ? text.Substring(0, 40) : text;
    Console.WriteLine($"{item.Index}: {item.Pointer.Serialize()} | {clip}");
}
