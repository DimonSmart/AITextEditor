using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using AiTextEditor.Lib.Services.SemanticKernel;
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
    public async Task QuestionAboutProfessor_ReturnsPointerToFirstMention()
    {
        var markdown = LoadNeznaykaSample();
        using var httpClient = CreateLlmClient();
        var engine = new SemanticKernelEngine(httpClient);

        var result = await engine.RunPointerQuestionAsync(markdown, "Где в книге впервые упоминается профессор Звездочкин?", "neznayka-sample");

        Assert.Equal("1.1.1.p21", result.LastTargetSet?.Targets.Single().Pointer.SemanticNumber);
        Assert.Contains(NormalizeForSearch("звездочкин"), NormalizeForSearch(result.LastTargetSet!.Targets.Single().Text), StringComparison.Ordinal);
        Assert.Contains("1.1.1.p21", result.LastAnswer, StringComparison.OrdinalIgnoreCase);
        output.WriteLine(string.Join("\n", result.UserMessages));
    }

    [Fact]
    public async Task QuestionAboutProfessor_WithAlternativeSpelling_ReturnsPointerToFirstMention()
    {
        var markdown = LoadNeznaykaSample();
        using var httpClient = CreateLlmClient();
        var engine = new SemanticKernelEngine(httpClient);

        var result = await engine.RunPointerQuestionAsync(markdown, "Покажи первое упоминание профессора ЗВЁЗДОЧКИНА в тексте.", "neznayka-sample-variant");

        Assert.Equal("1.1.1.p21", result.LastTargetSet?.Targets.Single().Pointer.SemanticNumber);
        Assert.Contains(NormalizeForSearch("звездочкин"), NormalizeForSearch(result.LastTargetSet!.Targets.Single().Text), StringComparison.Ordinal);
        Assert.Contains("1.1.1.p21", result.LastAnswer, StringComparison.OrdinalIgnoreCase);
        output.WriteLine(string.Join("\n", result.UserMessages));
    }

    [Fact]
    public async Task QuestionAboutProfessor_RewritesParagraphWithBlossomingApples()
    {
        var markdown = LoadNeznaykaSample();
        using var httpClient = CreateLlmClient();
        var engine = new SemanticKernelEngine(httpClient);

        var result = await engine.RunPointerQuestionAsync(
            markdown,
            "Найди первое упоминание профессора Звёздочкина и перепиши параграф, добавив, что в этот момент начали распускаться яблоки.",
            "neznayka-sample-rewrite");

        var answer = result.LastAnswer ?? string.Empty;

        Assert.Equal("1.1.1.p21", result.LastTargetSet?.Targets.Single().Pointer.SemanticNumber);
        Assert.Contains(NormalizeForSearch("яблоки"), NormalizeForSearch(answer), StringComparison.Ordinal);
        Assert.Contains(NormalizeForSearch("переписанный параграф"), NormalizeForSearch(answer), StringComparison.Ordinal);
        Assert.Contains("1.1.1.p21", answer, StringComparison.OrdinalIgnoreCase);
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

    private static HttpClient CreateLlmClient()
    {
        var baseUrl = Environment.GetEnvironmentVariable("LLM_BASE_URL") ?? "http://localhost:11434";
        var apiKey = Environment.GetEnvironmentVariable("LLM_API_KEY");
        var client = new HttpClient
        {
            BaseAddress = new Uri(baseUrl)
        };

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        return client;
    }
}
