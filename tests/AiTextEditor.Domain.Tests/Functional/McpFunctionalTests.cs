using System.Globalization;
using System.Text;
using AiTextEditor.Domain.Tests;
using AiTextEditor.Domain.Tests.Infrastructure;
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

    [Theory]
    [InlineData("Где в книге впервые упоминается профессор Звёздочкин? (исключая заголовки)", "1.1.1.p21")]
    [InlineData("Где в книге второе упоминание профессора Звёздочкина? (исключая заголовки)", "1.1.1.p26")]
    [InlineData("Где впервые упоминается Пончик?", "1.1.1.p3")]
    [InlineData("Найди первый диалог между двумя названными по имени персонажами и назови их имена", "1.1.1.p26")]
    [InlineData("Где впервые упоминается Фуксия?", "1.1.1.p5")]
    //[InlineData("Покажи последнее упоминание Фуксии.", "1.1.1.p67")]
    public async Task CharacterMentionQuestions_ReturnExpectedPointer(string question, string expectedPointer)
    {
        var markdown = LoadNeznaykaSample();
        using var httpClient = await TestLlmConfiguration.CreateVerifiedLlmClientAsync(output);
        using var loggerFactory = TestLoggerFactory.Create(output);
        var engine = new SemanticKernelEngine(httpClient, loggerFactory);

        var result = await engine.RunAsync(markdown, question);
        var answer = result.LastAnswer ?? string.Empty;

        Assert.Contains(expectedPointer, answer, StringComparison.OrdinalIgnoreCase);

        if (question.Contains("диалог"))
        {
            Assert.DoesNotContain("Alice", answer, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Rabbit", answer, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task QuestionAboutProfessor_RewritesParagraphWithBlossomingApples()
    {
        var markdown = LoadNeznaykaSample();
        using var httpClient = await TestLlmConfiguration.CreateVerifiedLlmClientAsync(output);
        using var loggerFactory = TestLoggerFactory.Create(output);
        var engine = new SemanticKernelEngine(httpClient, loggerFactory);

        var result = await engine.RunAsync(
            markdown,
            "Найди первое упоминание профессора Звёздочкина и перепиши параграф, добавив, что в этот момент начали распускаться яблони.");

        var answer = result.LastAnswer ?? string.Empty;

        Assert.Contains("1.1.1.p21", answer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(NormalizeForSearch("яблони"), NormalizeForSearch(answer), StringComparison.Ordinal);
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
