using AiTextEditor.Core.Model;
using AiTextEditor.Core.Services;
using AiTextEditor.Agent;
using Xunit;

namespace AiTextEditor.Tests;

public class McpServerTests
{

    [Fact]
    public void LoadDefaultDocument_AllowsExplicitId()
    {
        var server = new EditorSession();

        var document = server.LoadDefaultDocument("# Title", "custom-id");

        Assert.Equal("custom-id", document.Id);
        Assert.Equal("custom-id", server.GetDefaultDocument().Id);
    }

    [Fact]
    public void GetItems_ThrowsWhenDefaultDocumentMissing()
    {
        var server = new EditorSession();

        Assert.Throws<InvalidOperationException>(() => server.GetItems());
    }

    [Fact]
    public void GetItems_ReturnsLinearItems()
    {
        var server = new EditorSession();
        server.LoadDefaultDocument("# Title\n\nParagraph");

        var items = server.GetItems();

        Assert.Collection(
            items,
            item => Assert.Equal(LinearItemType.Heading, item.Type),
            item => Assert.Equal(LinearItemType.Paragraph, item.Type));
    }

    [Fact]
    public void CreateTargetSet_FiltersDuplicatesAndOutOfRange()
    {
        var server = new EditorSession();
        server.LoadDefaultDocument("# Title\n\nFirst paragraph\n\nSecond paragraph");

        var targetSet = server.CreateTargetSet(new[] { 2, 2, 5, -1 });

        Assert.Single(targetSet.Targets);
        Assert.Equal("1.p2", targetSet.Targets[0].Pointer.Label);
    }

    [Fact]
    public void ApplyOperations_ReindexesAndUpdatesSource()
    {
        var server = new EditorSession();
        var document = server.LoadDefaultDocument("# Title\n\nParagraph");
        var replacement = document.Items[0] with
        {
            Markdown = "# Updated",
            Text = "Updated"
        };

        var updated = server.ApplyOperations(new[]
        {
            new LinearEditOperation(LinearEditAction.Replace, document.Items[0].Pointer, null, new[] { replacement })
        });

        Assert.Equal(2, updated.Items.Count);
        Assert.Equal("Updated", updated.Items[0].Text);
        Assert.Equal("# Updated\n\nParagraph", updated.SourceText);
    }

    [Fact]
    public void CreateTargetSet_UsesLinearDocumentItems()
    {
        var server = new EditorSession();
        var document = server.LoadDefaultDocument("# Title\n\nParagraph one\n\nParagraph two");

        var targetSet = server.CreateTargetSet(new[] { 1, 2 }, "command", "label");

        Assert.Equal(2, targetSet.Targets.Count);
        Assert.Equal("1.p1", targetSet.Targets[0].Pointer.Label);
        Assert.Equal("1.p2", targetSet.Targets[1].Pointer.Label);
    }

    [Fact]
    public void LoadDefaultDocument_EnablesParameterlessOperations()
    {
        var server = new EditorSession();

        var document = server.LoadDefaultDocument("# Title\n\nParagraph one");
        var items = server.GetItems();

        Assert.Equal(document.Id, server.GetDefaultDocument().Id);
        Assert.Equal(2, items.Count);

        var replacement = items[0] with { Markdown = "# Updated", Text = "Updated" };

        var updated = server.ApplyOperations(new[]
        {
            new LinearEditOperation(LinearEditAction.Replace, items[0].Pointer, null, new[] { replacement })
        });

        Assert.Equal("# Updated\n\nParagraph one", updated.SourceText);
    }

    [Fact]
    public void TargetSetLifecycle_AllowsQueryingAndDeletion()
    {
        var server = new EditorSession();
        server.LoadDefaultDocument("# Title\n\nParagraph one\n\nParagraph two");

        var firstSet = server.CreateTargetSet(new[] { 1 }, label: "first");
        var secondSet = server.CreateTargetSet(new[] { 2 }, label: "second");

        var allSets = server.ListDefaultTargetSets();
        Assert.Equal(2, allSets.Count);

        var fetched = server.GetTargetSet(firstSet.Id);
        Assert.NotNull(fetched);
        Assert.Equal(firstSet.Id, fetched!.Id);
        Assert.Equal("first", fetched.Label);

        var deleted = server.DeleteTargetSet(firstSet.Id);
        Assert.True(deleted);
        Assert.Null(server.GetTargetSet(firstSet.Id));

        var remainingSets = server.ListDefaultTargetSets();
        Assert.Single(remainingSets);
        Assert.Equal(secondSet.Id, remainingSets[0].Id);
    }

    [Fact]
    public void DeleteTargetSet_ReturnsFalseForUnknownId()
    {
        var server = new EditorSession();

        Assert.False(server.DeleteTargetSet("missing"));
    }

    [Fact]
    public void ApplyOperations_ThrowsForUnknownTargetPointer()
    {
        var server = new EditorSession();
        var document = server.LoadDefaultDocument("# Title\n\nParagraph");

        var invalidPointer = new SemanticPointer("9.9.p9");
        var replacement = document.Items[0];

        Assert.Throws<InvalidOperationException>(() => server.ApplyOperations(new[]
        {
            new LinearEditOperation(LinearEditAction.Replace, invalidPointer, null, new[] { replacement })
        }));
    }
}
