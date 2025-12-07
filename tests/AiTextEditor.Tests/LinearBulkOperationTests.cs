using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AiTextEditor.Lib.Model;
using AiTextEditor.Lib.Services;
using Xunit;

namespace AiTextEditor.Tests;

public class LinearBulkOperationTests
{
    [Fact]
    public void ApplyLinearOperations_ReplacesByPointerAndRecalculates()
    {
        var repository = new MarkdownDocumentRepository();
        var document = repository.LoadFromMarkdown("""
        # Title

        First paragraph.

        Second paragraph.
        """);

        var target = document.LinearDocument.Items.First(i => i.Type == LinearItemType.Paragraph);
        var operation = new LinearEditOperation
        {
            Action = LinearEditAction.Replace,
            TargetPointer = target.Pointer,
            Items =
            {
                new LinearItem
                {
                    Markdown = "Rewritten paragraph.",
                    Text = "Rewritten paragraph.",
                    Type = LinearItemType.Paragraph
                }
            }
        };

        var editor = new DocumentEditor(repository);
        editor.ApplyLinearOperations(document, new[] { operation });

        var rewritten = document.Blocks.First(b => b.PlainText == "Rewritten paragraph.");
        Assert.Equal("1.p1", rewritten.StructuralPath);
        Assert.Contains(document.LinearDocument.Items, i => i.Text == "Rewritten paragraph." && i.Pointer.SemanticNumber == "1.p1");
        Assert.Contains("Rewritten paragraph.", document.SourceText);
    }

    [Fact]
    public async Task BulkReplace_ReportsProgressAndUpdatesDocument()
    {
        var repository = new MarkdownDocumentRepository();
        var document = repository.LoadFromMarkdown("""
        # Header

        First block.

        Second block.
        """);

        var targetService = new InMemoryTargetSetService();
        var targetSet = targetService.Create(document.Id, document.LinearDocument.Items);

        var bulkService = new BulkOperationService(new DocumentEditor(repository));
        var progress = new List<BulkOperationProgress>();

        await foreach (var step in bulkService.ReplaceTextInTargetsAsync(document, targetSet, item => $"Updated {item.Index}", batchSize: 2))
        {
            progress.Add(step);
        }

        Assert.NotEmpty(progress);
        Assert.Equal(targetSet.Targets.Count, progress.Last().Completed);
        Assert.Contains(document.LinearDocument.Items, i => i.Text == "Updated 0");
        Assert.Contains("Updated 1", document.SourceText);
    }
}
