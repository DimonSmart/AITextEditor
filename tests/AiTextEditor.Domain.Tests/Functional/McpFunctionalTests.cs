using AiTextEditor.Domain.Tests.Infrastructure;
using AiTextEditor.SemanticKernel;
using AiTextEditor.Lib.Common;
using AiTextEditor.Lib.Services;
using AiTextEditor.Lib.Services.SemanticKernel;
using Microsoft.Extensions.Logging;
using AiTextEditor.Lib.Model;
using DimonSmart.AiUtils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using System.Globalization;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;
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
    //[InlineData("Где в книге впервые упоминается профессор Звёздочкин? (исключая заголовки)", "1.1.1.p21")]
    [InlineData("Где в книге четвертое упоминание профессора Звёздочкина? (исключая заголовки)", "1.1.1.p25", 1)]
    [InlineData("Где в книге говорится, о чём была книжка Знайки про лунных коротышек?", "1.1.1.p19")]
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
        var engine = new SemanticKernelEngine(httpClient, loggerFactory);

        var result = await engine.RunAsync(markdown, question);
        var answer = result.LastAnswer ?? string.Empty;

        var expected = new SemanticPointer(expectedPointer);
        var matches = Regex.Matches(answer, @"\b\d+(?:\.\d+)*\.?p\d+\b", RegexOptions.IgnoreCase);

        var found = matches.Select(m => new SemanticPointer(m.Value))
                           .Any(p => p.IsCloseTo(expected, tolerance));

        Assert.True(found, $"Expected pointer close to {expectedPointer} (tolerance {tolerance}) not found in answer: {answer}");
    }

    [Theory]
    [InlineData(
        "Создай каталог персонажей книги. Используй инструменты character_roster.generate_character_dossiers и character_roster.get_character_roster. Верни только JSON каталога.",
        null,
        true)]
    [InlineData(
        "Создай каталог персонажей книги. Найди персонажа \"Знайка\" в каталоге и обнови его описание на \"Главный из коротышек\" через character_roster.upsert_character (используй characterId из каталога). Затем верни JSON каталога.",
        "В каталоге есть персонаж Знайка с описанием \"Главный из коротышек\".",
        true)]
    public async Task CharacterRosterCommandScenarios_ReturnExpectedCatalog(string command, string? llmCheck, bool enforce)
    {
        var markdown = LoadNeznaykaSample();
        using var httpClient = await TestLlmConfiguration.CreateVerifiedLlmClientAsync(output);
        using var loggerFactory = TestLoggerFactory.Create(output);
        var engine = new SemanticKernelEngine(httpClient, loggerFactory);

        var result = await engine.RunAsync(markdown, command);
        var answer = result.LastAnswer ?? string.Empty;
        if (string.IsNullOrWhiteSpace(llmCheck))
        {
            if (!TryExtractRosterJson(answer, out var rosterJson))
            {
                rosterJson = await BuildRosterJsonAsync(markdown, loggerFactory, httpClient);
            }

            answer = rosterJson;
            var outputPath = Path.Combine(AppContext.BaseDirectory, "character_roster_output.json");
            answer = FormatJson(answer);
            File.WriteAllText(outputPath, answer);
            output.WriteLine($"Character roster saved to {outputPath}");
        }

        var outputLimit = string.IsNullOrWhiteSpace(llmCheck) ? 12000 : 2000;
        output.WriteLine($"Character roster response: {TruncateForOutput(answer, outputLimit)}");

        if (!enforce || string.IsNullOrWhiteSpace(llmCheck))
        {
            return;
        }

        var evaluation = await LlmAssert.EvaluateAsync(httpClient, answer, llmCheck, output);
        Assert.True(evaluation.Pass, $"LLM assert failed: {evaluation.Reason}. Raw: {evaluation.RawResponse}");
    }

    [Fact]
    public async Task CharacterRosterCursorOrchestrator_ReturnsFullListForLargeCatalog()
    {
        var names = new[]
        {
            "Альфа",
            "Бета",
            "Гамма",
            "Дельта",
            "Эпсилон",
            "Дзета",
            "Эта",
            "Тета",
            "Йота",
            "Каппа",
            "Лямбда",
            "Мю",
            "Ню",
            "Кси",
            "Омикрон",
            "Пи",
            "Ро",
            "Сигма",
            "Тау",
            "Ипсилон"
        };

        var markdownBuilder = new StringBuilder("# Characters\n\n");
        foreach (var name in names)
        {
            markdownBuilder.AppendLine($"{name} отправился исследовать далёкий город и встретил старых знакомых.");
            markdownBuilder.AppendLine();
        }

        var markdown = markdownBuilder.ToString();
        var repository = new MarkdownDocumentRepository();
        var document = repository.LoadFromMarkdown(markdown);
        var rosterService = new CharacterRosterService();
        var documentContext = new DocumentContext(document, rosterService);

        using var loggerFactory = TestLoggerFactory.Create(output);

        var limits = new CursorAgentLimits
        {
            CharacterRosterMaxCharacters = 18,
            DefaultMaxFound = 10,
            MaxElements = 10,
            MaxBytes = 32_000
        };

        var generator = new CharacterRosterGenerator(
            documentContext,
            rosterService,
            limits,
            loggerFactory.CreateLogger<CharacterRosterGenerator>(),
            chatService: null);

        var directRoster = await generator.GenerateAsync();
        Assert.Equal(limits.CharacterRosterMaxCharacters, directRoster.Characters.Count);

        var evidence = names
            .Select((name, index) => new EvidenceItem($"1.1.p{index + 1}", $"{name} отправился исследовать далёкий город.", "characters"))
            .ToList();

        var orchestrator = new CharacterRosterCursorOrchestrator(
            documentContext,
            new CursorRegistry(),
            new FixedEvidenceCursorAgentRuntime(evidence),
            generator,
            limits,
            loggerFactory.CreateLogger<CharacterRosterCursorOrchestrator>());

        var plugin = new CharacterRosterPlugin(
            generator,
            orchestrator,
            rosterService,
            limits,
            loggerFactory.CreateLogger<CharacterRosterPlugin>());

        await plugin.GenerateCharacterRosterAsync();

        var roster = plugin.GetCharacterRoster();

        Assert.Equal(names.Length, roster.Characters.Count);
        Assert.Contains(roster.Characters, c => c.Name.Contains(names[0], StringComparison.OrdinalIgnoreCase));
        Assert.Contains(roster.Characters, c => c.Name.Contains(names[^1], StringComparison.OrdinalIgnoreCase));
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

    private static bool TryExtractRosterJson(string text, out string rosterJson)
    {
        rosterJson = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (var candidate in JsonExtractor.ExtractAllJsons(text))
        {
            if (IsRosterJson(candidate))
            {
                rosterJson = candidate;
                return true;
            }
        }

        return false;
    }

    private static bool IsRosterJson(string json)
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

    private static IChatCompletionService CreateChatService(HttpClient httpClient)
    {
        var builder = Kernel.CreateBuilder();

        var modelId = TestLlmConfiguration.ResolveModel();
        var baseUrl = Environment.GetEnvironmentVariable("LLM_BASE_URL");
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            baseUrl = httpClient.BaseAddress?.ToString();
        }

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            baseUrl = "http://localhost:11434";
        }

        var endpoint = baseUrl.TrimEnd('/');
        if (!endpoint.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            endpoint += "/v1";
        }

        var apiKey = Environment.GetEnvironmentVariable("LLM_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            apiKey = "ollama";
        }

        builder.AddOpenAIChatCompletion(
            modelId: modelId,
            apiKey: apiKey,
            endpoint: new Uri(endpoint),
            httpClient: httpClient);
        var kernel = builder.Build();
        return kernel.GetRequiredService<IChatCompletionService>();
    }

    private static async Task<string> BuildRosterJsonAsync(string markdown, ILoggerFactory loggerFactory, HttpClient httpClient)
    {
        var repository = new MarkdownDocumentRepository();
        var document = repository.LoadFromMarkdown(markdown);
        var rosterService = new CharacterRosterService();
        var documentContext = new DocumentContext(document, rosterService);
        var limits = new CursorAgentLimits();
        var chatService = CreateChatService(httpClient);

        var generator = new CharacterRosterGenerator(
            documentContext,
            rosterService,
            limits,
            loggerFactory.CreateLogger<CharacterRosterGenerator>(),
            chatService);
        var cursorStore = new CursorRegistry();
        var orchestrator = new CharacterRosterCursorOrchestrator(
            documentContext,
            cursorStore,
            new NoOpCursorAgentRuntime(),
            generator,
            limits,
            loggerFactory.CreateLogger<CharacterRosterCursorOrchestrator>());
        var plugin = new CharacterRosterPlugin(
            generator,
            orchestrator,
            rosterService,
            limits,
            loggerFactory.CreateLogger<CharacterRosterPlugin>());

        await plugin.GenerateCharacterDossiersAsync(useCursorAgent: false);
        var roster = plugin.GetCharacterRoster();
        return JsonSerializer.Serialize(roster, SerializationOptions.RelaxedCompact);
    }

    private sealed class NoOpCursorAgentRuntime : ICursorAgentRuntime
    {
        public Task<CursorAgentResult> RunAsync(string cursorName, CursorAgentRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<CursorAgentStepResult> RunStepAsync(CursorAgentRequest request, CursorPortionView portion, CursorAgentState state, int step, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class FixedEvidenceCursorAgentRuntime(IReadOnlyList<EvidenceItem> evidence) : ICursorAgentRuntime
    {
        public Task<CursorAgentResult> RunAsync(string cursorName, CursorAgentRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new CursorAgentResult(true, "ok", evidence, CursorComplete: true));
        }

        public Task<CursorAgentStepResult> RunStepAsync(CursorAgentRequest request, CursorPortionView portion, CursorAgentState state, int step, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}
