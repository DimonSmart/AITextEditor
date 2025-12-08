using System.Globalization;
using System.Text;
using AiTextEditor.Lib.Services.SemanticKernel;
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
        var cassettePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Cassettes", "semantic_kernel_first_mention.har");
        using var httpClient = CreateRecordedClient(cassettePath);
        var engine = new SemanticKernelEngine(httpClient);

        var result = await engine.RunPointerQuestionAsync(markdown, "Где в книге впервые упоминается профессор Звездочкин?", "neznayka-sample");

        Assert.Equal("1.1.1.p21", result.LastTargetSet?.Targets.Single().Pointer.SemanticNumber);
        Assert.Contains(NormalizeForSearch("звездочкин"), NormalizeForSearch(result.LastTargetSet!.Targets.Single().Text), StringComparison.Ordinal);
        Assert.Contains("1.1.1.p21", result.LastAnswer, StringComparison.OrdinalIgnoreCase);
        output.WriteLine(string.Join("\n", result.UserMessages));
    }

    private static string LoadNeznaykaSample()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "MarkdownBooks", "neznayka_sample.md");
        return File.ReadAllText(path);
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
