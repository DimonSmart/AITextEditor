using AiTextEditor.Lib.Model;
using AiTextEditor.Lib.Services;
using Xunit;

namespace AiTextEditor.Domain.Tests;

public class McpServerTests
{
    [Fact]
    public void ApplyOperations_ReindexesAndUpdatesSource()
    {
        var server = new McpServer();
        var document = server.LoadDocument("# Title\n\nParagraph");
        var replacement = document.Items[0] with
        {
            Markdown = "# Updated",
            Text = "Updated"
        };

        var updated = server.ApplyOperations(
            document.Id,
            new[] { new LinearEditOperation(LinearEditAction.Replace, document.Items[0].Pointer, null, new[] { replacement }) });

        Assert.Equal(2, updated.Items.Count);
        Assert.Equal("Updated", updated.Items[0].Text);
        Assert.Equal("# Updated\n\nParagraph", updated.SourceText);
    }

    [Fact]
    public void CreateTargetSet_UsesLinearDocumentItems()
    {
        var server = new McpServer();
        var document = server.LoadDocument("# Title\n\nParagraph one\n\nParagraph two");

        var targetSet = server.CreateTargetSet(document.Id, new[] { 1, 2 }, "command", "label");

        Assert.Equal(document.Id, targetSet.DocumentId);
        Assert.Equal(2, targetSet.Targets.Count);
        Assert.Equal("1.p1", targetSet.Targets[0].Pointer.SemanticNumber);
        Assert.Equal("1.p2", targetSet.Targets[1].Pointer.SemanticNumber);
    }
}
