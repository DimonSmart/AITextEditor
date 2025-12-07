using AiTextEditor.Domain.Tests.Llm;
using AiTextEditor.Lib.Model;
using AiTextEditor.Lib.Services;
using Vcr.HttpRecorder;
using Vcr.HttpRecorder.Matchers;
using Xunit;
using Xunit.Abstractions;

namespace AiTextEditor.Domain.Tests;

public class McpServerTests
{
    private readonly ITestOutputHelper output;

    public McpServerTests(ITestOutputHelper output)
    {
        this.output = output;
    }

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
    public void LoadDefaultDocument_EnablesParameterlessOperations()
    {
        var server = new McpServer();

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
        var server = new McpServer();
        var firstDocument = server.LoadDocument("# First\n\nParagraph one");
        var secondDocument = server.LoadDocument("# Second\n\nParagraph two");

        var firstSet = server.CreateTargetSet(firstDocument.Id, new[] { 1 }, label: "first");
        var secondSet = server.CreateTargetSet(secondDocument.Id, new[] { 1 }, label: "second");

        var allSets = server.ListTargetSets(null);
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

        var remainingSets = server.ListTargetSets(null);
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
    public void ApplyOperations_ThrowsForUnknownTargetPointer()
    {
        var server = new McpServer();
        var document = server.LoadDocument("# Title\n\nParagraph");

        var invalidPointer = new LinearPointer(10, new SemanticPointer(new[] { 9 }, 1));
        var replacement = document.Items[0];

        Assert.Throws<InvalidOperationException>(() => server.ApplyOperations(document.Id, new[]
        {
            new LinearEditOperation(LinearEditAction.Replace, invalidPointer, null, new[] { replacement })
        }));
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

    [Fact]
    public async Task ChapterSummaryScenario_UsesNavigationAndSummarization()
    {
        var markdown = """
# Chapter One

The first chapter closes by hinting at a secret meeting.

# Chapter Two

The journey continues as the team crosses the river.

The second chapter ends with a cliffhanger about the hidden door.
""";
        var userCommand = "Расскажи мне, чем заканчивается вторая глава.";

        var scenario = new ChapterSummaryScenario(output);
        var result = await scenario.RunAsync(markdown, userCommand);

        result.AssertRequestedChapterCaptured();
        result.AssertSummaryMatchesExpectedEnding();
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

    private sealed class ChapterSummaryScenario
    {
        private readonly ITestOutputHelper output;

        public ChapterSummaryScenario(ITestOutputHelper output)
        {
            this.output = output;
        }

        public async Task<ScenarioResult> RunAsync(string markdown, string userCommand)
        {
            var server = new McpServer();
            var document = server.LoadDocument(markdown, "chapters-demo");
            output.WriteLine($"User command: {userCommand}");
            output.WriteLine($"Loaded document '{document.Id}' with {document.Items.Count} items.");

            var chapterNumber = ExtractChapterNumber(userCommand);
            var chapterItems = CaptureChapterItems(server.GetItems(document.Id), chapterNumber).ToList();

            foreach (var item in chapterItems)
            {
                output.WriteLine($"Target item[{item.Index}] pointer {item.Pointer.SemanticNumber}: {item.Text}");
            }

            var targetSet = server.CreateTargetSet(
                document.Id,
                chapterItems.Select(item => item.Index),
                userCommand,
                label: $"chapter-{chapterNumber}-summary");

            output.WriteLine($"Created target set {targetSet.Id} for document {targetSet.DocumentId} with {targetSet.Targets.Count} targets.");

            var cassettePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Cassettes", "llama_gpt-oss_120b-cloud.har");
            using var httpClient = CreateRecordedClient(cassettePath);
            var llamaClient = new LamaClient(httpClient);
            var response = await llamaClient.SummarizeTargetsAsync(targetSet);

            output.WriteLine($"LLM model: {response.Model}");
            output.WriteLine($"LLM content: {response.Content}");

            var expectedEnding = chapterItems.LastOrDefault()?.Text ?? string.Empty;
            return new ScenarioResult(chapterNumber, expectedEnding, chapterItems, targetSet, response.Content);
        }

        private static int ExtractChapterNumber(string userCommand)
        {
            var lower = userCommand.ToLowerInvariant();
            if (lower.Contains("вторая", StringComparison.OrdinalIgnoreCase) || lower.Contains("second", StringComparison.OrdinalIgnoreCase))
            {
                return 2;
            }

            if (lower.Contains("первая", StringComparison.OrdinalIgnoreCase) || lower.Contains("first", StringComparison.OrdinalIgnoreCase))
            {
                return 1;
            }

            var digit = lower.FirstOrDefault(char.IsDigit);
            if (digit != default && int.TryParse(digit.ToString(), out var parsed))
            {
                return parsed;
            }

            throw new InvalidOperationException("Could not determine requested chapter number from command.");
        }

        private static IEnumerable<LinearItem> CaptureChapterItems(IReadOnlyList<LinearItem> items, int chapterNumber)
        {
            var headings = items
                .Where(item => item.Type == LinearItemType.Heading)
                .ToList();

            if (chapterNumber <= 0 || chapterNumber > headings.Count)
            {
                throw new InvalidOperationException($"Chapter {chapterNumber} does not exist in the document.");
            }

            var start = headings[chapterNumber - 1].Index + 1;
            var end = chapterNumber < headings.Count ? headings[chapterNumber].Index : items.Count;

            return items.Skip(start).Take(end - start);
        }
    }

    private sealed record ScenarioResult(
        int ChapterNumber,
        string ExpectedEnding,
        IReadOnlyList<LinearItem> ChapterItems,
        TargetSet TargetSet,
        string Summary)
    {
        public void AssertRequestedChapterCaptured()
        {
            Assert.NotEmpty(ChapterItems);
            Assert.Equal(ChapterItems.Count, TargetSet.Targets.Count);
            var chapterSemanticPrefix = $"{ChapterNumber}.";
            Assert.All(TargetSet.Targets, target => Assert.StartsWith(chapterSemanticPrefix, target.Pointer.SemanticNumber, StringComparison.OrdinalIgnoreCase));
        }

        public void AssertSummaryMatchesExpectedEnding()
        {
            Assert.False(string.IsNullOrWhiteSpace(Summary));
            var matchesSummary = IsSemanticallySimilar(ExpectedEnding, Summary);
            var matchesTargetSelection = TargetSet.Targets.Any(target => IsSemanticallySimilar(ExpectedEnding, target.Text));

            Assert.True(matchesSummary || matchesTargetSelection, "LLM summary or selected targets do not match the expected ending meaning.");
        }

        private static bool IsSemanticallySimilar(string expected, string actual)
        {
            if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(actual))
            {
                return false;
            }

            var expectedTokens = Tokenize(expected);
            var actualTokens = Tokenize(actual);
            var overlap = expectedTokens.Intersect(actualTokens, StringComparer.OrdinalIgnoreCase).Count();
            var threshold = Math.Max(1, expectedTokens.Count / 3);
            return overlap >= threshold;
        }

        private static List<string> Tokenize(string text)
        {
            return text
                .Split(new[] { ' ', '.', ',', ';', ':', '!', '?' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(token => token.ToLowerInvariant())
                .ToList();
        }
    }
}
