using System.Text;
using System.Text.Json;
using AiTextEditor.Core.Services;
using AiTextEditor.Agent;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiTextEditor.Tests;

public sealed class CharacterDossiersGeneratorTests
{
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
        var limits = new CursorAgentLimits
        {
            MaxElements = 256,
            MaxBytes = 1024 * 128,
            CharacterDossiersMaxCharacters = null
        };
        var extractionModelClient = new CapturingCharacterExtractionModelClient();

        var generator = new CharacterDossiersGenerator(
            documentContext,
            dossierService,
            limits,
            NullLogger<CharacterDossiersGenerator>.Instance,
            extractionModelClient);

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
        var limits = new CursorAgentLimits
        {
            MaxElements = 2,
            MaxBytes = 1024 * 128,
            BatchOverlapElements = 1,
            FullScanMaxElements = 20
        };
        var extractionModelClient = new CapturingCharacterExtractionModelClient();
        var generator = new CharacterDossiersGenerator(
            documentContext,
            dossierService,
            limits,
            NullLogger<CharacterDossiersGenerator>.Instance,
            extractionModelClient);
        var runner = new CharacterBibleWorkflowRunner(generator, NullLoggerFactory.Instance);

        await runner.RunAsync(new CharacterBibleWorkflowInput());

        Assert.Equal(4, extractionModelClient.Requests.Count);
        Assert.Equal(
            [
                ["p1", "p2"],
                ["p2", "p3"],
                ["p3", "p4"],
                ["p4", "p5"]
            ],
            extractionModelClient.Requests.Select(ReadPromptPointers).ToArray());
    }

    [Fact]
    public async Task DescriptionStability_WhenOnlyAliasesChange_DescriptionIsNotTouched()
    {
        var dossierService = new CharacterDossierService();

        dossierService.UpsertDossier(new AiTextEditor.Core.Model.CharacterDossier(
            CharacterId: "c1",
            Name: "John",
            Description: "John is a doctor.",
            Aliases: ["John"],
            AliasExamples: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["John"] = "John examined the patient."
            },
            Gender: "unknown"));

        var repository = new MarkdownDocumentRepository();
        var document = repository.LoadFromMarkdown("John arrived.");
        var documentContext = new DocumentContext(document, dossierService);
        var limits = new CursorAgentLimits { MaxElements = 256, MaxBytes = 1024 * 128 };

        var extractionModelClient = new ScriptedCharacterExtractionModelClient(
            Response(new CharacterExtractionCharacter(
                "John",
                "unknown",
                [new CharacterExtractionAlias("Johnny", "Johnny smiled.")],
                null)));

        var generator = new CharacterDossiersGenerator(
            documentContext,
            dossierService,
            limits,
            NullLogger<CharacterDossiersGenerator>.Instance,
            extractionModelClient);

        var evidence = new[] { new AiTextEditor.Core.Model.EvidenceItem("1.p1", "Johnny smiled.", null) };
        await generator.UpdateFromEvidenceBatchAsync(evidence);

        var updated = dossierService.TryGetDossier("c1");
        Assert.NotNull(updated);
        Assert.Equal("John is a doctor.", updated!.Description);
        Assert.Contains("Johnny", updated.AliasExamples.Keys, StringComparer.OrdinalIgnoreCase);

        Assert.Equal(1, extractionModelClient.CallCount);
    }

    [Fact]
    public async Task GenerateDossiers_ReplacesEmptyDescriptionWithMeaningfulDescription()
    {
        var dossierService = new CharacterDossierService();
        var repository = new MarkdownDocumentRepository();
        var document = repository.LoadFromMarkdown("John arrived.\n\nJohn stayed calm and helped Mary.");
        var documentContext = new DocumentContext(document, dossierService);
        var limits = new CursorAgentLimits { MaxElements = 1, MaxBytes = 1024 * 128, FullScanMaxElements = 10 };
        var extractionModelClient = new ScriptedCharacterExtractionModelClient(
            Response(new CharacterExtractionCharacter(
                "John",
                "unknown",
                [new CharacterExtractionAlias("John", "John arrived.")],
                "")),
            Response(new CharacterExtractionCharacter(
                "John",
                "unknown",
                [new CharacterExtractionAlias("John", "John stayed calm and helped Mary.")],
                "John stays calm under pressure and helps others.")));

        var generator = new CharacterDossiersGenerator(
            documentContext,
            dossierService,
            limits,
            NullLogger<CharacterDossiersGenerator>.Instance,
            extractionModelClient);

        var dossiers = await generator.GenerateAsync();

        var dossier = Assert.Single(dossiers.Characters);
        Assert.Equal("John stays calm under pressure and helps others.", dossier.Description);
    }

    [Fact]
    public async Task GenerateDossiers_DoesNotReplaceMeaningfulDescriptionWithEmptyDescription()
    {
        var dossierService = new CharacterDossierService();
        var repository = new MarkdownDocumentRepository();
        var document = repository.LoadFromMarkdown("John stayed calm.\n\nJohn arrived.");
        var documentContext = new DocumentContext(document, dossierService);
        var limits = new CursorAgentLimits { MaxElements = 1, MaxBytes = 1024 * 128, FullScanMaxElements = 10 };
        var extractionModelClient = new ScriptedCharacterExtractionModelClient(
            Response(new CharacterExtractionCharacter(
                "John",
                "unknown",
                [new CharacterExtractionAlias("John", "John stayed calm.")],
                "John stays calm under pressure.")),
            Response(new CharacterExtractionCharacter(
                "John",
                "unknown",
                [new CharacterExtractionAlias("John", "John arrived.")],
                "")));

        var generator = new CharacterDossiersGenerator(
            documentContext,
            dossierService,
            limits,
            NullLogger<CharacterDossiersGenerator>.Instance,
            extractionModelClient);

        var dossiers = await generator.GenerateAsync();

        var dossier = Assert.Single(dossiers.Characters);
        Assert.Equal("John stays calm under pressure.", dossier.Description);
    }

    [Fact]
    public async Task GenerateDossiers_MergesProfileWithoutOverwritingManualSections()
    {
        var dossierService = new CharacterDossierService();
        dossierService.UpsertDossier(new AiTextEditor.Core.Model.CharacterDossier(
            CharacterId: "c1",
            Name: "John",
            Description: "",
            Aliases: ["John"],
            AliasExamples: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["John"] = "John arrived."
            },
            Gender: "unknown",
            Profile: new AiTextEditor.Core.Model.CharacterProfile(
                PsychologicalProfile: "Manual psychological profile.",
                KeyRoleBonds:
                [
                    new AiTextEditor.Core.Model.CharacterRoleBond(
                        "Mary",
                        "mentor",
                        "Mary anchors John's training role.")
                ])));

        var repository = new MarkdownDocumentRepository();
        var document = repository.LoadFromMarkdown("John and Mary met Bob.");
        var documentContext = new DocumentContext(document, dossierService);
        var limits = new CursorAgentLimits { MaxElements = 256, MaxBytes = 1024 * 128 };
        var extractionModelClient = new ScriptedCharacterExtractionModelClient(
            Response(new CharacterExtractionCharacter(
                "John",
                "unknown",
                [new CharacterExtractionAlias("John", "John and Mary met Bob.")],
                "",
                ExtractionProfile(
                    appearance: "Tall and still.",
                    background: "Former student.",
                    psychology: "Generated profile should not overwrite manual text.",
                    speech: "Short answers.",
                    roleBonds:
                    [
                        new CharacterExtractionRoleBond("Mary", "mentor", "Duplicate should not replace manual bond."),
                        new CharacterExtractionRoleBond("Bob", "rival", "Bob defines John's competitive role.")
                    ]))));

        var generator = new CharacterDossiersGenerator(
            documentContext,
            dossierService,
            limits,
            NullLogger<CharacterDossiersGenerator>.Instance,
            extractionModelClient);

        var dossiers = await generator.GenerateAsync();

        var dossier = Assert.Single(dossiers.Characters);
        Assert.Equal("Tall and still.", dossier.Profile!.Appearance);
        Assert.Equal("Former student.", dossier.Profile.BackgroundStatusEducation);
        Assert.Equal("Manual psychological profile.", dossier.Profile.PsychologicalProfile);
        Assert.Equal("Short answers.", dossier.Profile.SpeechAndCommunication);
        var bonds = dossier.Profile.KeyRoleBonds!.ToDictionary(
            bond => $"{bond.CharacterName}|{bond.Role}",
            StringComparer.OrdinalIgnoreCase);
        Assert.Equal("Mary anchors John's training role.", bonds["Mary|mentor"].Description);
        Assert.Equal("Bob defines John's competitive role.", bonds["Bob|rival"].Description);
        Assert.Equal(2, bonds.Count);
    }

    [Fact]
    public async Task GenerateDossiers_PromptForbidsPronounAliases()
    {
        var dossierService = new CharacterDossierService();
        var repository = new MarkdownDocumentRepository();
        var document = repository.LoadFromMarkdown("Андерс оглянулся. Он услышал шум.");
        var documentContext = new DocumentContext(document, dossierService);
        var limits = new CursorAgentLimits { MaxElements = 256, MaxBytes = 1024 * 128 };
        var extractionModelClient = new CapturingCharacterExtractionModelClient();

        var generator = new CharacterDossiersGenerator(
            documentContext,
            dossierService,
            limits,
            NullLogger<CharacterDossiersGenerator>.Instance,
            extractionModelClient);

        await generator.GenerateAsync();

        Assert.NotNull(extractionModelClient.LastRequest);
        Assert.Contains("Pronouns are NEVER aliases", extractionModelClient.LastRequest!.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("он", extractionModelClient.LastRequest.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("она", extractionModelClient.LastRequest.SystemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateDossiers_PromptRequiresEmptyDescriptionsWhenPersonalityIsAbsent()
    {
        var dossierService = new CharacterDossierService();
        var repository = new MarkdownDocumentRepository();
        var document = repository.LoadFromMarkdown("Она стояла в толпе. У неё была родинка под левым глазом.");
        var documentContext = new DocumentContext(document, dossierService);
        var limits = new CursorAgentLimits { MaxElements = 256, MaxBytes = 1024 * 128 };
        var extractionModelClient = new CapturingCharacterExtractionModelClient();

        var generator = new CharacterDossiersGenerator(
            documentContext,
            dossierService,
            limits,
            NullLogger<CharacterDossiersGenerator>.Instance,
            extractionModelClient);

        await generator.GenerateAsync();

        Assert.NotNull(extractionModelClient.LastRequest);
        var systemPrompt = extractionModelClient.LastRequest!.SystemPrompt;
        Assert.Contains("The description field MUST be", systemPrompt, StringComparison.Ordinal);
        Assert.Contains("DO NOT explain that details are missing", systemPrompt, StringComparison.Ordinal);
        Assert.Contains("DO NOT retell scenes", systemPrompt, StringComparison.Ordinal);
        Assert.Contains("describe appearance", systemPrompt, StringComparison.Ordinal);
        Assert.Contains("what they saw", systemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateDossiers_PromptRequiresStructuredRussianProfile()
    {
        var dossierService = new CharacterDossierService();
        var repository = new MarkdownDocumentRepository();
        var document = repository.LoadFromMarkdown("Анна говорила коротко.");
        var documentContext = new DocumentContext(document, dossierService);
        var limits = new CursorAgentLimits { MaxElements = 256, MaxBytes = 1024 * 128 };
        var extractionModelClient = new CapturingCharacterExtractionModelClient();

        var generator = new CharacterDossiersGenerator(
            documentContext,
            dossierService,
            limits,
            NullLogger<CharacterDossiersGenerator>.Instance,
            extractionModelClient);

        await generator.GenerateAsync();

        Assert.NotNull(extractionModelClient.LastRequest);
        var systemPrompt = extractionModelClient.LastRequest!.SystemPrompt;
        Assert.Contains("character.profile", systemPrompt, StringComparison.Ordinal);
        Assert.Contains("Profile Rules", systemPrompt, StringComparison.Ordinal);
        Assert.Contains("Language: RUSSIAN", systemPrompt, StringComparison.Ordinal);
        Assert.Contains("keyRoleBonds", systemPrompt, StringComparison.Ordinal);
        Assert.Contains("role-defining relationships", systemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AliasExampleMerge_WhenAliasExists_DoesNotOverwriteExample()
    {
        var dossierService = new CharacterDossierService();
        dossierService.UpsertDossier(new AiTextEditor.Core.Model.CharacterDossier(
            CharacterId: "c1",
            Name: "John",
            Description: "",
            Aliases: ["Johnny"],
            AliasExamples: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Johnny"] = "Johnny waved."
            },
            Gender: "unknown"));

        var repository = new MarkdownDocumentRepository();
        var document = repository.LoadFromMarkdown("Johnny laughed.");
        var documentContext = new DocumentContext(document, dossierService);
        var limits = new CursorAgentLimits { MaxElements = 256, MaxBytes = 1024 * 128 };

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
            extractionModelClient);

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
        var limits = new CursorAgentLimits { MaxElements = 256, MaxBytes = 1024 * 128 };

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
            extractionModelClient);

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
        var limits = new CursorAgentLimits { MaxElements = 256, MaxBytes = 1024 * 128 };
        var extractionModelClient = new FailingCharacterExtractionModelClient("character_extraction_empty_response_content");

        var generator = new CharacterDossiersGenerator(
            documentContext,
            dossierService,
            limits,
            NullLogger<CharacterDossiersGenerator>.Instance,
            extractionModelClient);

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

    private static CharacterExtractionResponse Response(params CharacterExtractionCharacter[] characters)
        => new() { Characters = characters.ToList() };

    private static CharacterExtractionProfile ExtractionProfile(
        string appearance = "",
        string background = "",
        string psychology = "",
        string speech = "",
        List<CharacterExtractionRoleBond>? roleBonds = null)
    {
        return new CharacterExtractionProfile(
            appearance,
            background,
            psychology,
            speech,
            roleBonds ?? []);
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
                        [],
                        ""));
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
