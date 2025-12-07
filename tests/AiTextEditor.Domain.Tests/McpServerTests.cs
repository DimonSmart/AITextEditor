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

    [Fact]
    public void TargetSetLifecycle_AllowsQueryingAndDeletion()
    {
        var server = new McpServer();
        var firstDocument = server.LoadDocument("# First\n\nParagraph one");
        var secondDocument = server.LoadDocument("# Second\n\nParagraph two");

        var firstSet = server.CreateTargetSet(firstDocument.Id, new[] { 1 }, label: "first");
        var secondSet = server.CreateTargetSet(secondDocument.Id, new[] { 1 }, label: "second");

        var allSets = server.ListTargetSets();
        Assert.Equal(2, allSets.Count);

        var filteredSets = server.ListTargetSets(firstDocument.Id);
        Assert.Single(filteredSets);
        Assert.Equal(firstSet.Id, filteredSets[0].Id);

        var fetched = server.GetTargetSet(firstSet.Id);
        Assert.NotNull(fetched);
        Assert.Equal(firstSet.Id, fetched!.Id);
        Assert.Equal("first", fetched.Label);

        var deleted = server.DeleteTargetSet(firstSet.Id);
        Assert.True(deleted);
        Assert.Null(server.GetTargetSet(firstSet.Id));

        var remainingSets = server.ListTargetSets();
        Assert.Single(remainingSets);
        Assert.Equal(secondSet.Id, remainingSets[0].Id);
    }
}
