using System.Text;
using System.Text.Json;
using AiTextEditor.Core.Services;
using AiTextEditor.Agent;
using AiTextEditor.Agent.CharacterBible;
using AiTextEditor.Agent.CharacterBible.Extraction;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiTextEditor.Tests;

public sealed class CharacterDossiersGeneratorTests
{
    [Fact]
    public void CharacterExtractionPromptBuilder_LoadsPromptAndBuildsUserPromptJson()
    {
        var builder = new CharacterExtractionPromptBuilder();

        var systemPrompt = builder.BuildSystemPrompt();
        var userPrompt = builder.BuildUserPrompt(
            [
                ("p1", "Анна вошла."),
                ("p2", "Борис ответил.")
            ]);

        Assert.Contains("Pronouns are NEVER aliases", systemPrompt, StringComparison.Ordinal);
        Assert.Contains("name forms, nicknames, titles", systemPrompt, StringComparison.Ordinal);
        Assert.Contains("Use empty string when there is no evidence", systemPrompt, StringComparison.Ordinal);
        Assert.Contains("statusAndCompetence", systemPrompt, StringComparison.Ordinal);

        using var json = JsonDocument.Parse(userPrompt);
        Assert.Equal("extract_characters", json.RootElement.GetProperty("task").GetString());
        var paragraphs = json.RootElement.GetProperty("paragraphs").EnumerateArray().ToArray();
        Assert.Equal("p1", paragraphs[0].GetProperty("pointer").GetString());
        Assert.Equal("Анна вошла.", paragraphs[0].GetProperty("text").GetString());
        Assert.Equal("p2", paragraphs[1].GetProperty("pointer").GetString());
        Assert.Equal("Борис ответил.", paragraphs[1].GetProperty("text").GetString());
    }

    [Fact]
    public async Task GenerateDossiers_DoesNotTrimDossiersWhenLimitIsNotSet()
    {
        var names = new[]
        {
            "Алексей",
            "Борис",
            "Виктор",
            "Григорий",
            "Дмитрий",
            "Евгений",
            "Жанна",
            "Зоя",
            "Иван",
            "Кирилл",
            "Леонид",
            "Мария",
            "Никита",
            "Ольга",
            "Павел",
            "Роман",
            "Светлана",
            "Тимур",
            "Ульяна",
            "Фёдор"
        };

        var markdown = BuildMarkdown(names);
        var repository = new MarkdownDocumentRepository();
        var document = repository.LoadFromMarkdown(markdown);
        var dossierService = new CharacterDossierService();
        var documentContext = new DocumentContext(document, dossierService);
        var limits = new CharacterBibleExtractionLimits
        {
            MaxParagraphsPerBatch = 256,
            MaxBatchBytes = 1024 * 128,
            MaxCharacters = null
        };
        var extractionModelClient = new CapturingCharacterExtractionModelClient();

        var generator = new CharacterDossiersGenerator(
            documentContext,
            dossierService,
            limits,
            NullLogger<CharacterDossiersGenerator>.Instance,
            extractionModelClient,
            new CharacterExtractionPromptBuilder());

        var dossiers = await generator.GenerateAsync();

        Assert.Equal(names.Length, dossiers.Characters.Count);
        Assert.NotNull(extractionModelClient.LastRequest);

        using var json = JsonDocument.Parse(extractionModelClient.LastRequest.UserPrompt);
        var paragraphs = json.RootElement.GetProperty("paragraphs");
        Assert.Equal(names.Length, paragraphs.GetArrayLength());
    }

    [Fact]
    public async Task WorkflowExtraction_UsesConfiguredParagraphOverlap()
    {
        var names = new[] { "Анна", "Борис", "Вера", "Глеб", "Дина" };
        var repository = new MarkdownDocumentRepository();
        var document = repository.LoadFromMarkdown(BuildMarkdown(names));
        var dossierService = new CharacterDossierService();
        var documentContext = new DocumentContext(document, dossierService);
        var limits = new CharacterBibleExtractionLimits
        {
            MaxParagraphsPerBatch = 2,
            MaxBatchBytes = 1024 * 128,
            OverlapParagraphs = 1,
            FullScanMaxItems = 20
        };
        var extractionModelClient = new CapturingCharacterExtractionModelClient();
        var generator = new CharacterDossiersGenerator(
            documentContext,
            dossierService,
            limits,
            NullLogger<CharacterDossiersGenerator>.Instance,
            extractionModelClient,
            new CharacterExtractionPromptBuilder());
        var runner = new CharacterBibleWorkflowRunner(generator, NullLoggerFactory.Instance);

        await runner.RunAsync(new CharacterBibleWorkflowInput());

        Assert.Equal(3, extractionModelClient.Requests.Count);
        Assert.Equal(
            [
                ["p1", "p2"],
                ["p2", "p3", "p4"],
                ["p4", "p5"]
            ],
            extractionModelClient.Requests.Select(ReadPromptPointers).ToArray());
    }

    [Fact]
    public async Task WorkflowExtraction_LimitsOverlapByUtf8Bytes()
    {
        var extractionModelClient = await RunWorkflowExtractionAsync(
            BuildParagraphMarkdown(["aaaa", "bb", "cc", "dd", "ee"]),
            new CharacterBibleExtractionLimits
            {
                MaxParagraphsPerBatch = 2,
                MaxBatchBytes = 1024,
                OverlapParagraphs = 2,
                OverlapMaxBytes = 4,
                FullScanMaxItems = 20
            });

        Assert.Equal(
            [
                ["p1", "p2"],
                ["p2", "p3", "p4"],
                ["p3", "p4", "p5"]
            ],
            extractionModelClient.Requests.Select(ReadPromptPointers).ToArray());
    }

    [Fact]
    public async Task WorkflowExtraction_OverlapsOneParagraphWhenByteLimitIsSmallerThanLastParagraph()
    {
        var extractionModelClient = await RunWorkflowExtractionAsync(
            BuildParagraphMarkdown(["a", "bbbb", "c", "d"]),
            new CharacterBibleExtractionLimits
            {
                MaxParagraphsPerBatch = 2,
                MaxBatchBytes = 1024,
                OverlapParagraphs = 2,
                OverlapMaxBytes = 1,
                FullScanMaxItems = 20
            });

        Assert.Equal(
            [
                ["p1", "p2"],
                ["p2", "p3", "p4"]
            ],
            extractionModelClient.Requests.Select(ReadPromptPointers).ToArray());
    }

    [Fact]
    public async Task WorkflowExtraction_IgnoresOverlapByteLimitWhenItIsZero()
    {
        var extractionModelClient = await RunWorkflowExtractionAsync(
            BuildParagraphMarkdown(["aaaa", "bbbb", "cccc", "dddd", "eeee"]),
            new CharacterBibleExtractionLimits
            {
                MaxParagraphsPerBatch = 2,
                MaxBatchBytes = 1024,
                OverlapParagraphs = 2,
                OverlapMaxBytes = 0,
                FullScanMaxItems = 20
            });

        Assert.Equal(
            [
                ["p1", "p2"],
                ["p1", "p2", "p3", "p4"],
                ["p3", "p4", "p5"]
            ],
            extractionModelClient.Requests.Select(ReadPromptPointers).ToArray());
    }

    [Fact]
    public async Task WorkflowExtraction_DoesNotOverlapWhenOverlapParagraphsIsZero()
    {
        var extractionModelClient = await RunWorkflowExtractionAsync(
            BuildParagraphMarkdown(["a", "b", "c", "d", "e"]),
            new CharacterBibleExtractionLimits
            {
                MaxParagraphsPerBatch = 2,
                MaxBatchBytes = 1024,
                OverlapParagraphs = 0,
                OverlapMaxBytes = 1024,
                FullScanMaxItems = 20
            });

        Assert.Equal(
            [
                ["p1", "p2"],
                ["p3", "p4"],
                ["p5"]
            ],
            extractionModelClient.Requests.Select(ReadPromptPointers).ToArray());
    }

    [Fact]
    public async Task GenerateDossiers_MergesProfileWithoutOverwritingManualSections()
    {
        var dossierService = new CharacterDossierService();
        dossierService.UpsertDossier(new AiTextEditor.Core.Model.CharacterDossier(
            CharacterId: "c1",
            Name: "John",
            Aliases: ["John"],
            AliasExamples: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["John"] = "John arrived."
            },
            Gender: "unknown",
            Profile: new AiTextEditor.Core.Model.CharacterProfile(
                PsychologicalProfile: "Manual psychological profile.")));

        var repository = new MarkdownDocumentRepository();
        var document = repository.LoadFromMarkdown("John and Mary met Bob.");
        var documentContext = new DocumentContext(document, dossierService);
        var limits = new CharacterBibleExtractionLimits { MaxParagraphsPerBatch = 256, MaxBatchBytes = 1024 * 128 };
        var extractionModelClient = new ScriptedCharacterExtractionModelClient(
            Response(new CharacterExtractionCharacter(
                "John",
                "unknown",
                [new CharacterExtractionAlias("John", "John and Mary met Bob.")],
                ExtractionProfile(
                    appearance: "Tall and still.",
                    background: "Former student.",
                    psychology: "Generated profile should not overwrite manual text.",
                    speech: "Short answers."))));

        var generator = new CharacterDossiersGenerator(
            documentContext,
            dossierService,
            limits,
            NullLogger<CharacterDossiersGenerator>.Instance,
            extractionModelClient,
            new CharacterExtractionPromptBuilder());

        var dossiers = await generator.GenerateAsync();

        var dossier = Assert.Single(dossiers.Characters);
        Assert.Equal("Tall and still.", dossier.Profile!.Appearance);
        Assert.Equal("Former student.", dossier.Profile.StatusAndCompetence);
        Assert.Equal("Manual psychological profile.", dossier.Profile.PsychologicalProfile);
        Assert.Equal("Short answers.", dossier.Profile.SpeechAndCommunication);
    }

    [Fact]
    public async Task GenerateDossiers_PromptForbidsPronounAliases()
    {
        var dossierService = new CharacterDossierService();
        var repository = new MarkdownDocumentRepository();
        var document = repository.LoadFromMarkdown("Андерс оглянулся. Он услышал шум.");
        var documentContext = new DocumentContext(document, dossierService);
        var limits = new CharacterBibleExtractionLimits { MaxParagraphsPerBatch = 256, MaxBatchBytes = 1024 * 128 };
        var extractionModelClient = new CapturingCharacterExtractionModelClient();

        var generator = new CharacterDossiersGenerator(
            documentContext,
            dossierService,
            limits,
            NullLogger<CharacterDossiersGenerator>.Instance,
            extractionModelClient,
            new CharacterExtractionPromptBuilder());

        await generator.GenerateAsync();

        Assert.NotNull(extractionModelClient.LastRequest);
        Assert.Contains("Pronouns are NEVER aliases", extractionModelClient.LastRequest!.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("он", extractionModelClient.LastRequest.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("она", extractionModelClient.LastRequest.SystemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateDossiers_PromptRequiresEmptyProfileFieldsWhenEvidenceIsAbsent()
    {
        var dossierService = new CharacterDossierService();
        var repository = new MarkdownDocumentRepository();
        var document = repository.LoadFromMarkdown("Она стояла в толпе. У неё была родинка под левым глазом.");
        var documentContext = new DocumentContext(document, dossierService);
        var limits = new CharacterBibleExtractionLimits { MaxParagraphsPerBatch = 256, MaxBatchBytes = 1024 * 128 };
        var extractionModelClient = new CapturingCharacterExtractionModelClient();

        var generator = new CharacterDossiersGenerator(
            documentContext,
            dossierService,
            limits,
            NullLogger<CharacterDossiersGenerator>.Instance,
            extractionModelClient,
            new CharacterExtractionPromptBuilder());

        await generator.GenerateAsync();

        Assert.NotNull(extractionModelClient.LastRequest);
        var systemPrompt = extractionModelClient.LastRequest!.SystemPrompt;
        Assert.Contains("Use empty string when there is no evidence", systemPrompt, StringComparison.Ordinal);
        Assert.Contains("Do not retell scenes", systemPrompt, StringComparison.Ordinal);
        Assert.Contains("statusAndCompetence", systemPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("description field", systemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateDossiers_PromptRequiresStructuredRussianProfile()
    {
        var dossierService = new CharacterDossierService();
        var repository = new MarkdownDocumentRepository();
        var document = repository.LoadFromMarkdown("Анна говорила коротко.");
        var documentContext = new DocumentContext(document, dossierService);
        var limits = new CharacterBibleExtractionLimits { MaxParagraphsPerBatch = 256, MaxBatchBytes = 1024 * 128 };
        var extractionModelClient = new CapturingCharacterExtractionModelClient();

        var generator = new CharacterDossiersGenerator(
            documentContext,
            dossierService,
            limits,
            NullLogger<CharacterDossiersGenerator>.Instance,
            extractionModelClient,
            new CharacterExtractionPromptBuilder());

        await generator.GenerateAsync();

        Assert.NotNull(extractionModelClient.LastRequest);
        var systemPrompt = extractionModelClient.LastRequest!.SystemPrompt;
        Assert.Contains("character.profile", systemPrompt, StringComparison.Ordinal);
        Assert.Contains("Profile Rules", systemPrompt, StringComparison.Ordinal);
        Assert.Contains("Language: RUSSIAN", systemPrompt, StringComparison.Ordinal);
        Assert.Contains("statusAndCompetence", systemPrompt, StringComparison.Ordinal);
        Assert.Contains("Do NOT add relationship lists", systemPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("keyRoleBonds", systemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AliasExampleMerge_WhenAliasExists_DoesNotOverwriteExample()
    {
        var dossierService = new CharacterDossierService();
        dossierService.UpsertDossier(new AiTextEditor.Core.Model.CharacterDossier(
            CharacterId: "c1",
            Name: "John",
            Aliases: ["Johnny"],
            AliasExamples: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Johnny"] = "Johnny waved."
            },
            Gender: "unknown"));

        var repository = new MarkdownDocumentRepository();
        var document = repository.LoadFromMarkdown("Johnny laughed.");
        var documentContext = new DocumentContext(document, dossierService);
        var limits = new CharacterBibleExtractionLimits { MaxParagraphsPerBatch = 256, MaxBatchBytes = 1024 * 128 };

        var extractionModelClient = new ScriptedCharacterExtractionModelClient(
            Response(new CharacterExtractionCharacter(
                "John",
                "unknown",
                [new CharacterExtractionAlias("Johnny", "Johnny laughed.")],
                null)));

        var generator = new CharacterDossiersGenerator(
            documentContext,
            dossierService,
            limits,
            NullLogger<CharacterDossiersGenerator>.Instance,
            extractionModelClient,
            new CharacterExtractionPromptBuilder());

        var evidence = new[] { new AiTextEditor.Core.Model.EvidenceItem("1.p1", "Johnny laughed.", null) };
        await generator.UpdateFromEvidenceBatchAsync(evidence);

        var updated = dossierService.TryGetDossier("c1");
        Assert.NotNull(updated);
        Assert.Equal("Johnny waved.", updated!.AliasExamples["Johnny"]);
    }

    [Fact]
    public async Task PossessiveNormalization_AddsBaseFormForJohns()
    {
        var dossierService = new CharacterDossierService();
        var repository = new MarkdownDocumentRepository();
        var document = repository.LoadFromMarkdown("John's hat was on the table.");
        var documentContext = new DocumentContext(document, dossierService);
        var limits = new CharacterBibleExtractionLimits { MaxParagraphsPerBatch = 256, MaxBatchBytes = 1024 * 128 };

        var extractionModelClient = new ScriptedCharacterExtractionModelClient(
            Response(new CharacterExtractionCharacter(
                "John",
                "unknown",
                [new CharacterExtractionAlias("John's", "John's hat was on the table.")],
                null)));

        var generator = new CharacterDossiersGenerator(
            documentContext,
            dossierService,
            limits,
            NullLogger<CharacterDossiersGenerator>.Instance,
            extractionModelClient,
            new CharacterExtractionPromptBuilder());

        var evidence = new[] { new AiTextEditor.Core.Model.EvidenceItem("1.p1", "John's hat was on the table.", null) };
        var dossiers = await generator.UpdateFromEvidenceBatchAsync(evidence);

        Assert.Single(dossiers.Characters);
        var character = dossiers.Characters[0];
        Assert.Contains("John's", character.AliasExamples.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("John", character.AliasExamples.Keys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerateDossiers_WhenExtractionModelFails_PropagatesContractError()
    {
        var repository = new MarkdownDocumentRepository();
        var document = repository.LoadFromMarkdown("John arrived.");
        var dossierService = new CharacterDossierService();
        var documentContext = new DocumentContext(document, dossierService);
        var limits = new CharacterBibleExtractionLimits { MaxParagraphsPerBatch = 256, MaxBatchBytes = 1024 * 128 };
        var extractionModelClient = new FailingCharacterExtractionModelClient("character_extraction_empty_response_content");

        var generator = new CharacterDossiersGenerator(
            documentContext,
            dossierService,
            limits,
            NullLogger<CharacterDossiersGenerator>.Instance,
            extractionModelClient,
            new CharacterExtractionPromptBuilder());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => generator.GenerateAsync());
        Assert.Equal("character_extraction_empty_response_content", exception.Message);
    }

    private static string BuildMarkdown(IEnumerable<string> names)
    {
        var builder = new StringBuilder();
        foreach (var name in names)
        {
            builder.AppendLine($"{name} вместе с товарищами обсуждал планы экспедиции в соседний город. {name} внимательно слушал детали путешествия и готовил список вещей, которые пригодятся в дороге.");
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static string BuildParagraphMarkdown(IEnumerable<string> paragraphs)
    {
        var builder = new StringBuilder();
        foreach (var paragraph in paragraphs)
        {
            builder.AppendLine(paragraph);
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static async Task<CapturingCharacterExtractionModelClient> RunWorkflowExtractionAsync(
        string markdown,
        CharacterBibleExtractionLimits limits)
    {
        var repository = new MarkdownDocumentRepository();
        var document = repository.LoadFromMarkdown(markdown);
        var dossierService = new CharacterDossierService();
        var documentContext = new DocumentContext(document, dossierService);
        var extractionModelClient = new CapturingCharacterExtractionModelClient();
        var generator = new CharacterDossiersGenerator(
            documentContext,
            dossierService,
            limits,
            NullLogger<CharacterDossiersGenerator>.Instance,
            extractionModelClient,
            new CharacterExtractionPromptBuilder());
        var runner = new CharacterBibleWorkflowRunner(generator, NullLoggerFactory.Instance);

        await runner.RunAsync(new CharacterBibleWorkflowInput());

        return extractionModelClient;
    }

    private static CharacterExtractionResponse Response(params CharacterExtractionCharacter[] characters)
        => new() { Characters = characters.ToList() };

    private static CharacterExtractionProfile ExtractionProfile(
        string appearance = "",
        string background = "",
        string psychology = "",
        string speech = "")
    {
        return new CharacterExtractionProfile(
            appearance,
            background,
            psychology,
            speech);
    }

    private sealed class CapturingCharacterExtractionModelClient : ICharacterExtractionModelClient
    {
        public CharacterExtractionModelRequest? LastRequest { get; private set; }
        public List<CharacterExtractionModelRequest> Requests { get; } = [];
        public int CallCount { get; private set; }

        public Task<CharacterExtractionResponse> ExtractCharactersAsync(
            CharacterExtractionModelRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            Requests.Add(request);
            CallCount++;
            return Task.FromResult(BuildCharacterResponse(request.UserPrompt));
        }

        private static CharacterExtractionResponse BuildCharacterResponse(string? prompt)
        {
            if (string.IsNullOrWhiteSpace(prompt))
            {
                return Response();
            }

            try
            {
                using var json = JsonDocument.Parse(prompt);
                if (!json.RootElement.TryGetProperty("paragraphs", out var paragraphs) || paragraphs.ValueKind != JsonValueKind.Array)
                {
                    return Response();
                }

                var characters = new List<CharacterExtractionCharacter>();
                foreach (var paragraph in paragraphs.EnumerateArray())
                {
                    if (!paragraph.TryGetProperty("text", out var textElement) || textElement.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var text = textElement.GetString() ?? string.Empty;
                    var name = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                   .FirstOrDefault();
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    characters.Add(new CharacterExtractionCharacter(
                        name,
                        "unknown",
                        []));
                }

                return Response(characters.ToArray());
            }
            catch (JsonException)
            {
                return Response();
            }
        }
    }

    private static string[] ReadPromptPointers(CharacterExtractionModelRequest request)
    {
        using var json = JsonDocument.Parse(request.UserPrompt);
        return json.RootElement
            .GetProperty("paragraphs")
            .EnumerateArray()
            .Select(paragraph => paragraph.GetProperty("pointer").GetString() ?? string.Empty)
            .ToArray();
    }

    private sealed class ScriptedCharacterExtractionModelClient : ICharacterExtractionModelClient
    {
        private readonly Queue<CharacterExtractionResponse> responses;

        public int CallCount { get; private set; }

        public ScriptedCharacterExtractionModelClient(params CharacterExtractionResponse[] responses)
        {
            this.responses = new Queue<CharacterExtractionResponse>(responses);
        }

        public Task<CharacterExtractionResponse> ExtractCharactersAsync(
            CharacterExtractionModelRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            var response = responses.Count > 0 ? responses.Dequeue() : Response();
            return Task.FromResult(response);
        }
    }

    private sealed class FailingCharacterExtractionModelClient(string message) : ICharacterExtractionModelClient
    {
        public Task<CharacterExtractionResponse> ExtractCharactersAsync(
            CharacterExtractionModelRequest request,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException(message);
    }
}


