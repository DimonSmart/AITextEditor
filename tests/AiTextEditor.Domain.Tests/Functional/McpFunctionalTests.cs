using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using AiTextEditor.Lib.Model;
using AiTextEditor.Lib.Services;
using Xunit;
using Xunit.Abstractions;

namespace AiTextEditor.Domain.Tests.Functional;

public class McpFunctionalTests
{
    private readonly ITestOutputHelper output;

    public McpFunctionalTests(ITestOutputHelper output)
    {
        this.output = output;
    }

    [Fact]
    public void QuestionAboutProfessor_ReturnsPointerToFirstMention()
    {
        var markdown = LoadNeznaykaSample();
        var scenario = new SemanticPointerScenario(output);

        var result = scenario.Run(markdown, "Где в книге впервые упоминается профессор Звездочкин?");

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

        public ScenarioResult Run(string markdown, string question)
        {
            var server = new McpServer();
            var document = server.LoadDefaultDocument(markdown, "neznayka-sample");
            output.WriteLine($"Loaded document '{document.Id}' for question: {question}");

            var items = server.GetItems();
            var match = items.FirstOrDefault(item => ContainsNormalized(item.Text, "профессор звездочкин")
                || ContainsNormalized(item.Markdown, "профессор звездочкин"));

            if (match == null)
            {
                throw new InvalidOperationException("The target mention was not found in the document.");
            }

            output.WriteLine($"First mention found at index {match.Index} with pointer {match.Pointer.SemanticNumber}.");

            var targetSet = server.CreateTargetSet(new[] { match.Index }, question, label: "first-mention");
            return new ScenarioResult(match, targetSet);
        }

    }

    private sealed record ScenarioResult(LinearItem Target, TargetSet TargetSet)
    {
        public void AssertFirstMentionLocated()
        {
            const string expectedPointer = "1.1.1.p21";
            Assert.Equal(expectedPointer, Target.Pointer.SemanticNumber);
            Assert.Equal(Target.Pointer.SemanticNumber, TargetSet.Targets.Single().Pointer.SemanticNumber);
            Assert.Contains(NormalizeForSearch("звездочкин"), NormalizeForSearch(Target.Text), StringComparison.Ordinal);
        }
    }
}
