using AiTextEditor.Tests.Infrastructure;
using AiTextEditor.Agent;
using AiTextEditor.Core.Common;
using AiTextEditor.Core.Services;
using AiTextEditor.Core.Interfaces;
using Microsoft.Extensions.Logging;
using AiTextEditor.Core.Model;
using DimonSmart.AiUtils;
using System.Globalization;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace AiTextEditor.Tests.Functional;

public class McpFunctionalTests
{
    private readonly ITestOutputHelper output;

    public McpFunctionalTests(ITestOutputHelper output)
    {
        this.output = output;
    }

    [Theory]
    [Trait("Category", "Manual")]
    [InlineData("Где в книге впервые упоминается профессор Звёздочкин? (исключая заголовки)", "1.1.1.p21")]
    //[InlineData("Где в книге четвертое упоминание профессора Звёздочкина? (исключая заголовки)", "1.1.1.p25", 1)]
    //[InlineData("Где в книге говорится, о чём была книжка Знайки про лунных коротышек?", "1.1.1.p19")]
    //[InlineData("Где впервые упоминается Пончик?", "1.1.1.p3")]
    //[InlineData("Найди первый диалог между двумя названными по имени персонажами и назови их имена", "1.1.1.p68",2)]
    //[InlineData("Где впервые упоминается Фуксия?", "1.1.1.p5")]
    //[InlineData("Кто такие Фуксия и Селедочка и где они упоминаются впервые?", "1.1.1.p5")]
    //[InlineData("Покажи последнее упоминание Фуксии.", "1.1.1.p67")]
    //[InlineData("В каком месте описаны два вертящихся здания на улице Колокольчиков, со спиральным спуском и чёртовым колесом на крыше?", "1.1.1.p1")]
    //[InlineData("Где перечисляется, что одёжная фабрика выпускала всё подряд, от резиновых лифчиков до зимних шуб из синтетического волокна?", "1.1.1.p2")]
    //[InlineData("В каком абзаце Пончик, выбрав потемней ночку, утопил старые костюмы в Огурцовой реке?", "1.1.1.p3")]
    //[InlineData("Где объясняется, почему от Пончика все разбегались из-за нафталина и хозяева распахивали окна и двери?", "1.1.1.p4")]
    //[InlineData("Где сказано, что Знайка пробыл на Луне около четырёх часов и пришлось срочно возвращаться из-за воздуха?", "1.1.1.p5")]
    //[InlineData("В каком месте даётся “визуализация” лунного кратера как огромного круглого поля с земляным валом по краю?", "1.1.1.p6")]
    //[InlineData("Где описано, как астрономы в Солнечном городе поссорились и разделились на вулканистов и метеоритчиков?", "1.1.1.p7")]
    //[InlineData("В каком абзаце Знайка сравнивает лунную поверхность с хорошо пропечённым блином и объясняет пузырьки пара?", "1.1.1.p8")]
    //[InlineData("Где впервые появляется профессор Звёздочкин, который “кипел от негодования”, и кратко описывается, какой он по характеру?", "1.1.1.p21")]
    //[InlineData("В каком месте Знайка язвительно поддевает профессора фразой про то, что тому якобы приходилось болтаться в центре Луны?", "1.1.1.p27")]
    public async Task UserQueryScenarios_ReturnExpectedPointer(string question, string expectedPointer, int tolerance = 0)
    {
        var markdown = LoadNeznaykaSample();
        using var httpClient = await TestLlmConfiguration.CreateVerifiedLlmClientAsync(output);
        using var loggerFactory = TestLoggerFactory.Create(output);
        var engine = new AgenticWorkflowEngine(httpClient, loggerFactory, "35dac0c0480c47738f24e3a8ac12250a");

        var result = await engine.RunAsync(markdown, question);
        var answer = result.LastAnswer ?? string.Empty;

        var expected = new SemanticPointer(expectedPointer);
        var matches = Regex.Matches(answer, @"\b\d+(?:\.\d+)*\.?p\d+\b", RegexOptions.IgnoreCase);

        var found = matches.Select(m => new SemanticPointer(m.Value))
                           .Any(p => p.IsCloseTo(expected, tolerance));

        Assert.True(found, $"Expected pointer close to {expectedPointer} (tolerance {tolerance}) not found in answer: {answer}");
    }

    [Theory]
    [Trait("Category", "Manual")]
    [InlineData(
        "Создай каталог досье персонажей книги. Используй инструменты character_dossiers.generate_character_dossiers и character_dossiers.get_character_dossiers. Верни только JSON каталога.",
        null,
        true,
        null)]
//    [InlineData(
//        "Создай каталог досье персонажей книги. Найди персонажа \"Знайка\" и обнови его через character_dossiers.upsert_character_dossier (используй characterId из каталога). Затем верни JSON каталога.",
//        "В каталоге есть персонаж Знайка с описанием \"Главный из коротышек\".",
//        true)]
    public async Task CharacterDossiersCommandScenarios_ReturnExpectedCatalog(string command, string? llmCheck, bool enforce, string? modelName)
    {
        var markdown = LoadNeznaykaSample();
        using var httpClient = await TestLlmConfiguration.CreateVerifiedLlmClientAsync(output);
        using var loggerFactory = TestLoggerFactory.Create(output);
        var engine = new AgenticWorkflowEngine(httpClient, loggerFactory, "35dac0c0480c47738f24e3a8ac12250a", modelName);

        var result = await engine.RunAsync(markdown, command);
        var answer = result.LastAnswer ?? string.Empty;
        if (string.IsNullOrWhiteSpace(llmCheck))
        {
            Assert.True(
                TryExtractDossiersJson(answer, out var dossiersJson),
                $"Expected command response to contain character dossiers JSON. Response: {TruncateForOutput(answer, 4000)}");

            var outputPath = Path.Combine(AppContext.BaseDirectory, "character_dossiers_output.json");
            answer = FormatJson(dossiersJson);
            File.WriteAllText(outputPath, answer, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            output.WriteLine($"Character dossiers saved to {outputPath}");
        }

        var outputLimit = string.IsNullOrWhiteSpace(llmCheck) ? 12000 : 2000;
        output.WriteLine($"Character dossiers response: {TruncateForOutput(answer, outputLimit)}");

        if (!enforce || string.IsNullOrWhiteSpace(llmCheck))
        {
            return;
        }

        var evaluation = await LlmAssert.EvaluateAsync(httpClient, answer, llmCheck, output);
        Assert.True(evaluation.Pass, $"LLM assert failed: {evaluation.Reason}. Raw: {evaluation.RawResponse}");
    }

  
    private static string LoadNeznaykaSample()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "MarkdownBooks", "neznayka_sample.md");
        return File.ReadAllText(path);
    }

   
    private static string TruncateForOutput(string text, int maxLength = 2000)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
        {
            return text;
        }

        return text[..maxLength] + $"... (+{text.Length - maxLength} chars)";
    }

    private static string FormatJson(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
        }
        catch
        {
            return text;
        }
    }

    private static bool TryExtractDossiersJson(string text, out string dossiersJson)
    {
        dossiersJson = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (var candidate in JsonExtractor.ExtractAllJsons(text))
        {
            if (IsDossiersJson(candidate))
            {
                dossiersJson = candidate;
                return true;
            }
        }

        return false;
    }

    private static bool IsDossiersJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object &&
                   root.TryGetProperty("characters", out var characters) &&
                   characters.ValueKind == JsonValueKind.Array;
        }
        catch
        {
            return false;
        }
    }

    private sealed class TokenizingCharacterExtractionModelClient : ICharacterExtractionModelClient
    {
        public Task<CharacterExtractionResponse> ExtractCharactersAsync(
            CharacterExtractionModelRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(BuildCharacterResponse(request.UserPrompt));
        }

        private static CharacterExtractionResponse BuildCharacterResponse(string? prompt)
        {
            if (string.IsNullOrWhiteSpace(prompt))
            {
                return new CharacterExtractionResponse();
            }

            try
            {
                using var json = JsonDocument.Parse(prompt);
                if (!json.RootElement.TryGetProperty("paragraphs", out var paragraphs) || paragraphs.ValueKind != JsonValueKind.Array)
                {
                    return new CharacterExtractionResponse();
                }

                var characters = new List<CharacterExtractionCharacter>();
                foreach (var paragraph in paragraphs.EnumerateArray())
                {
                    if (!paragraph.TryGetProperty("text", out var textElement) || textElement.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var text = textElement.GetString() ?? string.Empty;
                    var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    foreach (var token in tokens)
                    {
                        var name = token.Trim(',', '.', ';', ':', '"', '\'');
                        if (string.IsNullOrWhiteSpace(name))
                        {
                            continue;
                        }

                        characters.Add(new CharacterExtractionCharacter(name, "unknown", [], string.Empty));
                    }
                }

                return new CharacterExtractionResponse { Characters = characters };
            }
            catch (JsonException)
            {
                return new CharacterExtractionResponse();
            }
        }
    }
}
