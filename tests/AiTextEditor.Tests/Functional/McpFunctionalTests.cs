using AiTextEditor.Tests.Infrastructure;
using AiTextEditor.Agent;
using AiTextEditor.Core.Common;
using AiTextEditor.Core.Services;
using AiTextEditor.Core.Interfaces;
using Microsoft.Extensions.Logging;
using AiTextEditor.Core.Model;
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
        var engine = new SemanticKernelEngine(httpClient, loggerFactory, "35dac0c0480c47738f24e3a8ac12250a");

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
        true)]
//    [InlineData(
//        "Создай каталог досье персонажей книги. Найди персонажа \"Знайка\" и обнови его через character_dossiers.upsert_character_dossier (используй characterId из каталога). Затем верни JSON каталога.",
//        "В каталоге есть персонаж Знайка с описанием \"Главный из коротышек\".",
//        true)]
    public async Task CharacterDossiersCommandScenarios_ReturnExpectedCatalog(string command, string? llmCheck, bool enforce)
    {
        var markdown = LoadNeznaykaSample();
        using var httpClient = await TestLlmConfiguration.CreateVerifiedLlmClientAsync(output);
        using var loggerFactory = TestLoggerFactory.Create(output);
        var engine = new SemanticKernelEngine(httpClient, loggerFactory, "35dac0c0480c47738f24e3a8ac12250a");

        var result = await engine.RunAsync(markdown, command);
        var answer = result.LastAnswer ?? string.Empty;
        if (string.IsNullOrWhiteSpace(llmCheck))
        {
            if (!TryExtractDossiersJson(answer, out var dossiersJson))
            {
                dossiersJson = await BuildDossiersJsonAsync(markdown, loggerFactory, httpClient);
            }

            answer = dossiersJson;
            var outputPath = Path.Combine(AppContext.BaseDirectory, "character_dossiers_output.json");
            answer = FormatJson(answer);
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

    [Fact]
    public async Task CharacterDossiersGenerator_RespectsMaxCharacters()
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
        var dossierService = new CharacterDossierService();
        var documentContext = new DocumentContext(document, dossierService);

        using var loggerFactory = TestLoggerFactory.Create(output);

        var limits = new CursorAgentLimits
        {
            CharacterDossiersMaxCharacters = 18,
            DefaultMaxFound = 10,
            MaxElements = 10,
            MaxBytes = 32_000
        };

        var chatService = new TokenizingChatCompletionService();
        var generator = new CharacterDossiersGenerator(
            documentContext,
            dossierService,
            limits,
            loggerFactory.CreateLogger<CharacterDossiersGenerator>(),
            chatService);

        var directDossiers = await generator.GenerateAsync();
        var maxCharacters = limits.CharacterDossiersMaxCharacters ?? names.Length;
        var expectedCount = System.Math.Min(names.Length, maxCharacters);
        Assert.Equal(expectedCount, directDossiers.Characters.Count);

        Assert.Equal(expectedCount, directDossiers.Characters.Count);
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

    private static async Task<string> BuildDossiersJsonAsync(string markdown, ILoggerFactory loggerFactory, HttpClient httpClient)
    {
        var repository = new MarkdownDocumentRepository();
        var document = repository.LoadFromMarkdown(markdown);
        var dossierService = new CharacterDossierService();
        var documentContext = new DocumentContext(document, dossierService);
        var limits = new CursorAgentLimits();
        var chatService = CreateChatService(httpClient);

        var generator = new CharacterDossiersGenerator(
            documentContext,
            dossierService,
            limits,
            loggerFactory.CreateLogger<CharacterDossiersGenerator>(),
            chatService);

        var plugin = new CharacterDossiersPlugin(
            generator,
            new CursorRegistry(),
            dossierService,
            limits,
            loggerFactory.CreateLogger<CharacterDossiersPlugin>());

        await plugin.GenerateCharacterDossiersAsync();
        var dossiers = plugin.GetCharacterDossiers();
        return JsonSerializer.Serialize(dossiers, SerializationOptions.RelaxedCompact);
    }

    private sealed class TokenizingChatCompletionService : IChatCompletionService
    {
        public IReadOnlyDictionary<string, object?> Attributes { get; } = new Dictionary<string, object?>();

        public Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
            ChatHistory chatHistory,
            PromptExecutionSettings? executionSettings = null,
            Kernel? kernel = null,
            CancellationToken cancellationToken = default)
        {
            var prompt = chatHistory.LastOrDefault(m => m.Role == AuthorRole.User)?.Content;
            var payload = BuildCharacterPayload(prompt);
            var result = new List<ChatMessageContent>
            {
                new(AuthorRole.Assistant, payload)
            };

            return Task.FromResult<IReadOnlyList<ChatMessageContent>>(result);
        }

        public IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
            ChatHistory chatHistory,
            PromptExecutionSettings? executionSettings = null,
            Kernel? kernel = null,
            CancellationToken cancellationToken = default)
        {
            return AsyncEnumerable.Empty<StreamingChatMessageContent>();
        }

        private static string BuildCharacterPayload(string? prompt)
        {
            if (string.IsNullOrWhiteSpace(prompt))
            {
                return "[]";
            }

            try
            {
                using var json = JsonDocument.Parse(prompt);
                if (!json.RootElement.TryGetProperty("paragraphs", out var paragraphs) || paragraphs.ValueKind != JsonValueKind.Array)
                {
                    return "[]";
                }

                var characters = new List<Dictionary<string, object?>>();
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

                        characters.Add(new Dictionary<string, object?>
                        {
                            ["canonicalName"] = name,
                            ["gender"] = "unknown",
                            ["aliases"] = Array.Empty<object>()
                        });
                    }
                }

                return JsonSerializer.Serialize(characters);
            }
            catch
            {
                return "[]";
            }
        }
    }
}
