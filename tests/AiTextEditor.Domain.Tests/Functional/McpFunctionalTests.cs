using System.Globalization;
using System.Text;
using AiTextEditor.Domain.Tests;
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
        using var httpClient = await TestLlmConfiguration.CreateVerifiedLlmClientAsync(output);
        var engine = new SemanticKernelEngine(httpClient);

        var result = await engine.RunAsync(markdown, "Где в книге впервые упоминается профессор Звездочкин?");

        // Note: The new engine relies on the LLM to return the answer.
        // We check if the answer contains the pointer.
        // The plugin returns "1.1.1.p21", so the LLM should include it.
        Assert.Contains("1.1.1.p21", result.LastAnswer, StringComparison.OrdinalIgnoreCase);
        output.WriteLine(string.Join("\n", result.UserMessages));
    }

    [Fact]
    public async Task QuestionAboutProfessor_WithAlternativeSpelling_ReturnsPointerToFirstMention()
    {
        var markdown = LoadNeznaykaSample();
        using var httpClient = await TestLlmConfiguration.CreateVerifiedLlmClientAsync(output);
        var engine = new SemanticKernelEngine(httpClient);

        var result = await engine.RunAsync(markdown, "Покажи первое упоминание профессора ЗВЁЗДОЧКИНА в тексте.");

        Assert.Contains("1.1.1.p21", result.LastAnswer, StringComparison.OrdinalIgnoreCase);
        output.WriteLine(string.Join("\n", result.UserMessages));
    }

    [Fact]
    public async Task QuestionAboutProfessor_RewritesParagraphWithBlossomingApples()
    {
        var markdown = LoadNeznaykaSample();
        using var httpClient = await TestLlmConfiguration.CreateVerifiedLlmClientAsync(output);
        var engine = new SemanticKernelEngine(httpClient);

        var result = await engine.RunAsync(
            markdown,
            "Найди первое упоминание профессора Звёздочкина и перепиши параграф, добавив, что в этот момент начали распускаться яблоки.");

        var answer = result.LastAnswer ?? string.Empty;

        Assert.Contains("1.1.1.p21", answer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(NormalizeForSearch("яблоки"), NormalizeForSearch(answer), StringComparison.Ordinal);
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
}
