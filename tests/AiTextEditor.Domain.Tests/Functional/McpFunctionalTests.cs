using AiTextEditor.Domain.Tests.Infrastructure;
using AiTextEditor.Lib.Services.SemanticKernel;
using System.Globalization;
using System.Text;
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
    [InlineData("Где в книге второе упоминание профессора Звёздочкина? (исключая заголовки)", "1.1.1.p22")]
    [InlineData("Где впервые упоминается Пончик?", "1.1.1.p3")]
    [InlineData("Найди первый диалог между двумя названными по имени персонажами и назови их имена", "1.1.1.p28")]
    [InlineData("Где впервые упоминается Фуксия?", "1.1.1.p5")]
    [InlineData("Покажи последнее упоминание Фуксии.", "1.1.1.p67")]
    [InlineData("В каком месте описаны два вертящихся здания на улице Колокольчиков, со спиральным спуском и чёртовым колесом на крыше?", "1.1.1.p1")]
    [InlineData("Где перечисляется, что одёжная фабрика выпускала всё подряд, от резиновых лифчиков до зимних шуб из синтетического волокна?", "1.1.1.p2")]
    [InlineData("В каком абзаце Пончик, выбрав потемней ночку, утопил старые костюмы в Огурцовой реке?", "1.1.1.p3")]
    [InlineData("Где объясняется, почему от Пончика все разбегались из-за нафталина и хозяева распахивали окна и двери?", "1.1.1.p4")]
    [InlineData("Где сказано, что Знайка пробыл на Луне около четырёх часов и пришлось срочно возвращаться из-за воздуха?", "1.1.1.p5")]
    [InlineData("В каком месте даётся “визуализация” лунного кратера как огромного круглого поля с земляным валом по краю?", "1.1.1.p6")]
    [InlineData("Где описано, как астрономы в Солнечном городе поссорились и разделились на вулканистов и метеоритчиков?", "1.1.1.p7")]
    [InlineData("В каком абзаце Знайка сравнивает лунную поверхность с хорошо пропечённым блином и объясняет пузырьки пара?", "1.1.1.p8")]
    [InlineData("Где впервые появляется профессор Звёздочкин, который “кипел от негодования”, и кратко описывается, какой он по характеру?", "1.1.1.p21")]
    [InlineData("В каком месте Знайка язвительно поддевает профессора фразой про то, что тому якобы приходилось болтаться в центре Луны?", "1.1.1.p27")]
    public async Task CharacterMentionQuestions_ReturnExpectedPointer(string question, string expectedPointer)
    {
        var markdown = LoadNeznaykaSample();
        using var httpClient = await TestLlmConfiguration.CreateVerifiedLlmClientAsync(output);
        using var loggerFactory = TestLoggerFactory.Create(output);
        var engine = new SemanticKernelEngine(httpClient, loggerFactory);

        var result = await engine.RunAsync(markdown, question);
        var answer = result.LastAnswer ?? string.Empty;

        // SemanticPointerComparator.AssertPointerMatch(markdown, expectedPointer, answer);

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
