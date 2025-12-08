using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using AiTextEditor.Domain.Tests.Llm;
using AiTextEditor.Lib.Model;
using AiTextEditor.Lib.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Xunit;
using Xunit.Abstractions;
using Vcr.HttpRecorder;
using Vcr.HttpRecorder.Matchers;

namespace AiTextEditor.Domain.Tests.Functional;

public class McpFunctionalTests
{
    private readonly ITestOutputHelper output;

    public McpFunctionalTests(ITestOutputHelper output)
    {
        this.output = output;
    }

    [Fact]
    public async Task QuestionAboutProfessor_ReturnsPointerToFirstMention()
    {
        var markdown = LoadNeznaykaSample();
        var scenario = new SemanticPointerScenario(output);

        var result = await scenario.RunAsync(markdown, "Где в книге впервые упоминается профессор Звездочкин?");

        result.AssertFirstMentionLocated();
    }

    private static string LoadNeznaykaSample()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "MarkdownBooks", "neznayka_sample.md");
        return File.ReadAllText(path);
    }

    private static bool ContainsNormalized(string text, string query)
    {
        var normalizedText = NormalizeForSearch(text);
        var normalizedQuery = NormalizeForSearch(query);
        return normalizedText.Contains(normalizedQuery, StringComparison.Ordinal);
    }

    private static string NormalizeForSearch(string text)
    {
        var builder = new StringBuilder();
        foreach (var character in text.Normalize(NormalizationForm.FormD))
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }

    private sealed class SemanticPointerScenario
    {
        private readonly ITestOutputHelper output;

        public SemanticPointerScenario(ITestOutputHelper output)
        {
            this.output = output;
        }

        public async Task<ScenarioResult> RunAsync(string markdown, string question)
        {
            var server = new McpServer();
            var document = server.LoadDefaultDocument(markdown, "neznayka-sample");
            output.WriteLine($"Loaded document '{document.Id}' for question: {question}");

            var kernel = CreateKernel(server);
            var items = server.GetItems();
            var formattedItems = FormatItems(items);
            var chatService = kernel.GetRequiredService<IChatCompletionService>();
            var history = new ChatHistory();

            history.AddSystemMessage("You are a literary assistant working with a Markdown MCP server. Each item has a semantic pointer like '1.1.1.p21'. Use the provided functions to reason about the document and answer exactly which pointer contains the first mention of Professor Zvezdochkin.");
            history.AddSystemMessage($"Available MCP functions: {string.Join(", ", kernel.Plugins.SelectMany(p => p.Select(f => f.Name)))}");
            history.AddSystemMessage($"Document items with pointers:\n{formattedItems}");
            history.AddUserMessage(question);

            var responses = await chatService.GetChatMessageContentsAsync(history, new PromptExecutionSettings(), kernel);
            var answer = responses.FirstOrDefault()?.Content ?? string.Empty;

            var pointer = ExtractPointer(answer);
            var match = items.First(item => string.Equals(item.Pointer.SemanticNumber, pointer, StringComparison.OrdinalIgnoreCase));

            output.WriteLine($"LLM answer: {answer}");
            output.WriteLine($"First mention resolved to pointer {pointer} at index {match.Index}.");

            var targetSet = server.CreateTargetSet(new[] { match.Index }, question, label: "first-mention");
            return new ScenarioResult(match, targetSet, answer, formattedItems);
        }

        private Kernel CreateKernel(McpServer server)
        {
            var cassettePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Cassettes", "semantic_kernel_first_mention.har");
            var httpClient = CreateRecordedClient(cassettePath);
            var llamaClient = new LamaClient(httpClient);
            var builder = Kernel.CreateBuilder();

            builder.Services.AddSingleton(llamaClient);
            builder.Services.AddSingleton<IChatCompletionService, LlamaSemanticKernelChatService>();
            builder.Plugins.AddFromObject(new McpServerPlugin(server));

            return builder.Build();
        }

        private static string FormatItems(IReadOnlyList<LinearItem> items)
        {
            var builder = new StringBuilder();
            foreach (var item in items)
            {
                builder.AppendLine($"{item.Pointer.SemanticNumber}: {item.Text}");
            }

            return builder.ToString();
        }

        private static string ExtractPointer(string answer)
        {
            var match = Regex.Match(answer, @"\d+(?:\.\d+)*\.p\d+", RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                throw new InvalidOperationException($"LLM response did not contain a semantic pointer: '{answer}'.");
            }

            return match.Value;
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

    private sealed record ScenarioResult(LinearItem Target, TargetSet TargetSet, string Answer, string Items)
    {
        public void AssertFirstMentionLocated()
        {
            const string expectedPointer = "1.1.1.p21";
            Assert.Equal(expectedPointer, Target.Pointer.SemanticNumber);
            Assert.Equal(Target.Pointer.SemanticNumber, TargetSet.Targets.Single().Pointer.SemanticNumber);
            Assert.Contains(NormalizeForSearch("звездочкин"), NormalizeForSearch(Target.Text), StringComparison.Ordinal);
            Assert.Contains(expectedPointer, Answer, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(expectedPointer, Items, StringComparison.OrdinalIgnoreCase);
        }
    }
}
