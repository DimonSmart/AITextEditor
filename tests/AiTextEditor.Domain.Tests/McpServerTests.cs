using AiTextEditor.Domain.Tests.Llm;
using AiTextEditor.Lib.Model;
using AiTextEditor.Lib.Services;
using Vcr.HttpRecorder;
using Vcr.HttpRecorder.Matchers;
using Xunit;

namespace AiTextEditor.Domain.Tests;

public class McpServerTests
{
    [Fact]
    public void LoadDocument_AllowsExplicitId()
    {
        var server = new McpServer();

        var document = server.LoadDocument("# Title", "custom-id");

        Assert.Equal("custom-id", document.Id);
        Assert.Same(document, server.GetDocument("custom-id"));
    }

    [Fact]
    public void GetDocument_ReturnsNullForUnknownId()
    {
        var server = new McpServer();

        Assert.Null(server.GetDocument("missing"));
    }

    [Fact]
    public void GetItems_ThrowsWhenDocumentMissing()
    {
        var server = new McpServer();

        Assert.Throws<InvalidOperationException>(() => server.GetItems("absent"));
    }

    [Fact]
    public void GetItems_ReturnsLinearItems()
    {
        var server = new McpServer();
        var document = server.LoadDocument("# Title\n\nParagraph");

        var items = server.GetItems(document.Id);

        Assert.Collection(
            items,
            item => Assert.Equal(LinearItemType.Heading, item.Type),
            item => Assert.Equal(LinearItemType.Paragraph, item.Type));
    }

    [Fact]
    public void CreateTargetSet_FiltersDuplicatesAndOutOfRange()
    {
        var server = new McpServer();
        var document = server.LoadDocument("# Title\n\nFirst paragraph\n\nSecond paragraph");

        var targetSet = server.CreateTargetSet(document.Id, new[] { 2, 2, 5, -1 });

        Assert.Single(targetSet.Targets);
        Assert.Equal("1.p2", targetSet.Targets[0].Pointer.SemanticNumber);
    }

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

    [Fact]
    public void DeleteTargetSet_ReturnsFalseForUnknownId()
    {
        var server = new McpServer();

        Assert.False(server.DeleteTargetSet("missing"));
    }

    [Fact]
    public async Task SemanticAction_UsesVcrBackedLamaClient()
    {
        var server = new McpServer();
        var document = server.LoadDocument("# Heading\n\nParagraph one\n\nParagraph two");
        var targetSet = server.CreateTargetSet(document.Id, new[] { 1, 2 });

        var cassettePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Cassettes", "llama_gpt-oss_120b-cloud.har");
        using var httpClient = CreateRecordedClient(cassettePath);

        var llamaClient = new LamaClient(httpClient);
        var response = await llamaClient.SummarizeTargetsAsync(targetSet);

        Assert.Equal("gpt-oss:120b-cloud", response.Model);
        Assert.Contains("Summary: combined targets", response.Content);
    }

    private static HttpClient CreateRecordedClient(string cassettePath)
    {
        var recorderHandler = new HttpRecorderDelegatingHandler(
            cassettePath,
            HttpRecorderMode.Replay,
            matcher: RulesMatcher.MatchMultiple
                .ByHttpMethod()
                .ByRequestUri(UriPartial.Path))
        {
            InnerHandler = new HttpClientHandler()
        };

        return new HttpClient(recorderHandler)
        {
            BaseAddress = new Uri("http://localhost:11434")
        };
    }
}
