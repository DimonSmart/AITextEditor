using AiTextEditor.Domain.Tests.Infrastructure;
using AiTextEditor.SemanticKernel;
using AiTextEditor.Lib.Model;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace AiTextEditor.Domain.Tests.Functional;

public class DialogTest
{
    private readonly ITestOutputHelper output;

    public DialogTest(ITestOutputHelper output)
    {
        this.output = output;
    }

    [Fact]
    public async Task FindFirstDialog_ReturnsExpectedPointer()
    {
        var question = "Найди первый диалог между двумя названными по имени персонажами и назови их имена";
        var expectedPointer = "1.1.1.p28";
        var tolerance = 2;

        var markdown = LoadNeznaykaSample();
        using var httpClient = await TestLlmConfiguration.CreateVerifiedLlmClientAsync(output);
        using var loggerFactory = TestLoggerFactory.Create(output);
        var engine = new SemanticKernelEngine(httpClient, loggerFactory);

        var result = await engine.RunAsync(markdown, question);
        var answer = result.LastAnswer ?? string.Empty;

        var expected = new SemanticPointer(expectedPointer);
        var matches = Regex.Matches(answer, @"\b\d+(?:\.\d+)*\.?p\d+\b", RegexOptions.IgnoreCase);

        var found = matches.Select(m => new SemanticPointer(m.Value))
                           .Any(p => p.IsCloseTo(expected, tolerance));

        Assert.True(found, $"Expected pointer close to {expectedPointer} (tolerance {tolerance}) not found in answer: {answer}");
    }

    private static string LoadNeznaykaSample()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "MarkdownBooks", "neznayka_sample.md");
        return File.ReadAllText(path);
    }
}
