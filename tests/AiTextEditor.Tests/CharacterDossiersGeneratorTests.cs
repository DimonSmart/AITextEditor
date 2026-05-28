using System.Text;
using System.Text.Json;
using AiTextEditor.Core.Services;
using AiTextEditor.Core.Model;
using AiTextEditor.Agent;
using AiTextEditor.Agent.CharacterBible;
using AiTextEditor.Agent.CharacterBible.Extraction;
using AiTextEditor.Agent.CharacterBible.Patching;
using AiTextEditor.Agent.CharacterBible.Resolution;
using AiTextEditor.Agent.CharacterBible.VectorSearch;
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
        Assert.Contains("exact observed name forms", systemPrompt, StringComparison.Ordinal);
        Assert.Contains("Do not return excerpts", systemPrompt, StringComparison.Ordinal);
        Assert.Contains("Do not return alias-level evidence", systemPrompt, StringComparison.Ordinal);
        Assert.Contains("Do not return character-level evidence", systemPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("character.profile", systemPrompt, StringComparison.Ordinal);

        using var json = JsonDocument.Parse(userPrompt);
        Assert.Equal("extract_character_candidates", json.RootElement.GetProperty("task").GetString());
        var paragraphs = json.RootElement.GetProperty("paragraphs").EnumerateArray().ToArray();
        Assert.Equal("p1", paragraphs[0].GetProperty("pointer").GetString());
        Assert.Equal("Анна вошла.", paragraphs[0].GetProperty("text").GetString());
        Assert.Equal("p2", paragraphs[1].GetProperty("pointer").GetString());
        Assert.Equal("Борис ответил.", paragraphs[1].GetProperty("text").GetString());
    }

    [Fact]
    public void DossierPatchPromptBuilder_LoadsPromptAndBuildsUserPromptJson()
    {
        var builder = new DossierPatchPromptBuilder();
        var candidate = new CharacterBibleCharacterCandidate(
            "John",
            "unknown",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Johnny"] = "Johnny entered."
            },
            [new CharacterBibleCandidateEvidence("p1", "Johnny entered.")]);
        var decision = new CharacterBibleResolverDecision(
            "John",
            CharacterBibleDecisionKind.New,
            "c1",
            [],
            "No existing name or alias match was found.");
        var dossier = new AiTextEditor.Core.Model.CharacterDossier(
            "c1",
            "John",
            ["Johnny"],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Johnny"] = "Johnny entered."
            },
            "unknown");

        var systemPrompt = builder.BuildSystemPrompt();
        var patchCandidate = new CharacterBibleDossierPatchCandidate(
            candidate,
            [
                new CharacterBibleEvidenceContext(
                    "p1",
                    "Johnny entered.",
                    "Johnny entered and answered briefly.",
                    "Johnny entered and answered briefly.",
                    [new CharacterBibleNearbyParagraph("p2", "Mary watched.", "next")])
            ]);
        var userPrompt = builder.BuildUserPrompt([patchCandidate], decision, dossier);

        Assert.Contains("DossierPatchProposalAgent", systemPrompt, StringComparison.Ordinal);
        Assert.Contains("aliasesToAdd", systemPrompt, StringComparison.Ordinal);
        Assert.Contains("Use null for a field that has no new additive information", systemPrompt, StringComparison.Ordinal);
        Assert.Contains("Profile field values are additive patches", systemPrompt, StringComparison.Ordinal);
        Assert.Contains("Do not use an empty string as a delete", systemPrompt, StringComparison.Ordinal);
        Assert.Contains("Do not wrap it in Markdown fences", systemPrompt, StringComparison.Ordinal);
        Assert.Contains("Never return pointer strings", systemPrompt, StringComparison.Ordinal);

        using var json = JsonDocument.Parse(userPrompt);
        Assert.Equal("propose_dossier_patch", json.RootElement.GetProperty("task").GetString());
        var promptCandidate = json.RootElement.GetProperty("candidates").EnumerateArray().Single();
        Assert.Equal("John", promptCandidate.GetProperty("canonicalName").GetString());
        var context = promptCandidate.GetProperty("evidenceContexts").EnumerateArray().Single();
        Assert.Equal("Johnny entered.", context.GetProperty("anchorExcerpt").GetString());
        Assert.Equal("Johnny entered and answered briefly.", context.GetProperty("currentParagraph").GetString());
        var nearby = context.GetProperty("nearbyParagraphs").EnumerateArray().Single();
        Assert.Equal("p2", nearby.GetProperty("pointer").GetString());
        Assert.Equal("c1", json.RootElement.GetProperty("identityDecision").GetProperty("targetEntryId").GetString());
        Assert.Equal("John", json.RootElement.GetProperty("dossier").GetProperty("name").GetString());
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
            new CharacterExtractionPromptBuilder(),
            NoopPatchClient(),
            new DossierPatchPromptBuilder(),
            ApprovingReviewerClient(),
            new DossierConsistencyReviewerPromptBuilder(),
            NewIdentityResolverClient(),
            NewVectorSearchTool());

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
            new CharacterExtractionPromptBuilder(),
            NoopPatchClient(),
            new DossierPatchPromptBuilder(),
            ApprovingReviewerClient(),
            new DossierConsistencyReviewerPromptBuilder(),
            NewIdentityResolverClient(),
            NewVectorSearchTool());
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
    public async Task GenerateDossiers_DoesNotWriteProfileFromExtraction()
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
            Response(Character(
                "John",
                Alias("John", "John and Mary met Bob."))));

        var generator = new CharacterDossiersGenerator(
            documentContext,
            dossierService,
            limits,
            NullLogger<CharacterDossiersGenerator>.Instance,
            extractionModelClient,
            new CharacterExtractionPromptBuilder(),
            NoopPatchClient(),
            new DossierPatchPromptBuilder(),
            ApprovingReviewerClient(),
            new DossierConsistencyReviewerPromptBuilder(),
            NewIdentityResolverClient(),
            NewVectorSearchTool());

        var dossiers = await generator.GenerateAsync();

        var dossier = Assert.Single(dossiers.Characters);
        Assert.Equal(string.Empty, dossier.Profile!.Appearance);
        Assert.Equal(string.Empty, dossier.Profile.StatusAndCompetence);
        Assert.Equal("Manual psychological profile.", dossier.Profile.PsychologicalProfile);
        Assert.Equal(string.Empty, dossier.Profile.SpeechAndCommunication);
        Assert.Equal("John arrived.", dossier.AliasExamples["John"]);
    }

    [Fact]
    public async Task GenerateDossiers_AppliesProfilePatchWithoutOverwritingManualSections()
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
        var document = repository.LoadFromMarkdown("John stood tall and answered briefly.");
        var documentContext = new DocumentContext(document, dossierService);
        var limits = new CharacterBibleExtractionLimits { MaxParagraphsPerBatch = 256, MaxBatchBytes = 1024 * 128 };
        var extractionModelClient = new ScriptedCharacterExtractionModelClient(
            Response(Character(
                "John",
                Alias("John", "John stood tall and answered briefly."))));
        var patchClient = new ScriptedDossierPatchProposalModelClient(
            ReadyPatch(
                appearance: "Высокая спокойная фигура.",
                statusAndCompetence: "Держится уверенно.",
                psychologicalProfile: null,
                speechAndCommunication: "Отвечает кратко."));

        var generator = new CharacterDossiersGenerator(
            documentContext,
            dossierService,
            limits,
            NullLogger<CharacterDossiersGenerator>.Instance,
            extractionModelClient,
            new CharacterExtractionPromptBuilder(),
            patchClient,
            new DossierPatchPromptBuilder(),
            ApprovingReviewerClient(),
            new DossierConsistencyReviewerPromptBuilder(),
            NewIdentityResolverClient(),
            NewVectorSearchTool());

        var dossiers = await generator.GenerateAsync();

        var dossier = Assert.Single(dossiers.Characters);
        Assert.Equal("Высокая спокойная фигура.", dossier.Profile!.Appearance);
        Assert.Equal("Держится уверенно.", dossier.Profile.StatusAndCompetence);
        Assert.Equal("Manual psychological profile.", dossier.Profile.PsychologicalProfile);
        Assert.Equal("Отвечает кратко.", dossier.Profile.SpeechAndCommunication);
        Assert.Equal(1, patchClient.CallCount);
    }

    [Fact]
    public async Task GenerateDossiers_AppendsProfilePatchToExistingSections()
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
        var document = repository.LoadFromMarkdown("John calmly helped the group.");
        var documentContext = new DocumentContext(document, dossierService);
        var limits = new CharacterBibleExtractionLimits { MaxParagraphsPerBatch = 256, MaxBatchBytes = 1024 * 128 };
        var extractionModelClient = new ScriptedCharacterExtractionModelClient(
            Response(Character(
                "John",
                Alias("John", "John calmly helped the group."))));
        var patchClient = new ScriptedDossierPatchProposalModelClient(
            ReadyPatch(psychologicalProfile: "Помогает группе спокойно."));

        var generator = new CharacterDossiersGenerator(
            documentContext,
            dossierService,
            limits,
            NullLogger<CharacterDossiersGenerator>.Instance,
            extractionModelClient,
            new CharacterExtractionPromptBuilder(),
            patchClient,
            new DossierPatchPromptBuilder(),
            ApprovingReviewerClient(),
            new DossierConsistencyReviewerPromptBuilder(),
            NewIdentityResolverClient(),
            NewVectorSearchTool());

        var dossiers = await generator.GenerateAsync();

        var dossier = Assert.Single(dossiers.Characters);
        Assert.Equal(
            "Manual psychological profile. Помогает группе спокойно.",
            dossier.Profile!.PsychologicalProfile);
    }

    [Fact]
    public async Task GenerateDossiers_PatchPromptExpandsAnchorPointerToNearbyDialogue()
    {
        var dossierService = new CharacterDossierService();
        var repository = new MarkdownDocumentRepository();
        var document = repository.LoadFromMarkdown("А Незнайка сказал:\n\n– Мы будем сегодня завтракать?");
        var documentContext = new DocumentContext(document, dossierService);
        var limits = new CharacterBibleExtractionLimits { MaxParagraphsPerBatch = 256, MaxBatchBytes = 1024 * 128 };
        var extractionModelClient = new ScriptedCharacterExtractionModelClient(
            Response(new ExtractedLocalCharacter(
                "Незнайка",
                "male",
                ["Незнайка"],
                ["p1"])));
        var patchClient = new ScriptedDossierPatchProposalModelClient();

        var generator = new CharacterDossiersGenerator(
            documentContext,
            dossierService,
            limits,
            NullLogger<CharacterDossiersGenerator>.Instance,
            extractionModelClient,
            new CharacterExtractionPromptBuilder(),
            patchClient,
            new DossierPatchPromptBuilder(),
            ApprovingReviewerClient(),
            new DossierConsistencyReviewerPromptBuilder(),
            NewIdentityResolverClient(),
            NewVectorSearchTool());

        await generator.GenerateAsync();

        var request = Assert.Single(patchClient.Requests);
        using var json = JsonDocument.Parse(request.UserPrompt);
        var promptCandidate = json.RootElement.GetProperty("candidates").EnumerateArray().Single();
        var context = promptCandidate.GetProperty("evidenceContexts").EnumerateArray().Single();
        Assert.Equal("p1", context.GetProperty("pointer").GetString());
        Assert.Equal("А Незнайка сказал:", context.GetProperty("anchorExcerpt").GetString());
        Assert.Equal("А Незнайка сказал:", context.GetProperty("currentParagraph").GetString());

        var nearbyParagraphs = context.GetProperty("nearbyParagraphs").EnumerateArray().ToArray();
        Assert.Contains(nearbyParagraphs, paragraph =>
            paragraph.GetProperty("position").GetString() == "next"
            && paragraph.GetProperty("text").GetString() == "– Мы будем сегодня завтракать?");
    }

    [Fact]
    public async Task GenerateDossiers_PatchPromptGroupsResolvedCandidatesByDossier()
    {
        var dossierService = new CharacterDossierService();
        var repository = new MarkdownDocumentRepository();
        var document = repository.LoadFromMarkdown("John stopped.\n\nJohn answered calmly.");
        var documentContext = new DocumentContext(document, dossierService);
        var limits = new CharacterBibleExtractionLimits { MaxParagraphsPerBatch = 256, MaxBatchBytes = 1024 * 128 };
        var extractionModelClient = new ScriptedCharacterExtractionModelClient(
            Response(
                new ExtractedLocalCharacter(
                    "John",
                    "male",
                    ["John"],
                    ["p1"]),
                new ExtractedLocalCharacter(
                    "John",
                    "male",
                    ["John"],
                    ["p2"])
            ));
        var patchClient = new ScriptedDossierPatchProposalModelClient();

        var generator = new CharacterDossiersGenerator(
            documentContext,
            dossierService,
            limits,
            NullLogger<CharacterDossiersGenerator>.Instance,
            extractionModelClient,
            new CharacterExtractionPromptBuilder(),
            patchClient,
            new DossierPatchPromptBuilder(),
            ApprovingReviewerClient(),
            new DossierConsistencyReviewerPromptBuilder(),
            NewIdentityResolverClient(),
            NewVectorSearchTool());

        await generator.GenerateAsync();

        var request = Assert.Single(patchClient.Requests);
        using var json = JsonDocument.Parse(request.UserPrompt);
        var candidates = json.RootElement.GetProperty("candidates").EnumerateArray().ToArray();
        Assert.Equal(2, candidates.Length);

        var contextPointers = candidates
            .SelectMany(candidate => candidate.GetProperty("evidenceContexts").EnumerateArray())
            .Select(context => context.GetProperty("pointer").GetString() ?? string.Empty)
            .OrderBy(pointer => pointer, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(["p1", "p2"], contextPointers);
        Assert.Equal(1, patchClient.CallCount);
    }

    [Fact]
    public async Task GenerateDossiers_PatchPromptSplitsLargeGroupedEvidenceBatches()
    {
        var dossierService = new CharacterDossierService();
        var repository = new MarkdownDocumentRepository();
        var firstParagraph = "John " + new string('a', 7_000);
        var secondParagraph = "John " + new string('b', 7_000);
        var document = repository.LoadFromMarkdown($"{firstParagraph}\n\n{secondParagraph}");
        var documentContext = new DocumentContext(document, dossierService);
        var limits = new CharacterBibleExtractionLimits { MaxParagraphsPerBatch = 256, MaxBatchBytes = 1024 * 128 };
        var extractionModelClient = new ScriptedCharacterExtractionModelClient(
            Response(
                new ExtractedLocalCharacter(
                    "John",
                    "male",
                    ["John"],
                    ["p1"]),
                new ExtractedLocalCharacter(
                    "John",
                    "male",
                    ["John"],
                    ["p2"])
            ));
        var patchClient = new ScriptedDossierPatchProposalModelClient();

        var generator = new CharacterDossiersGenerator(
            documentContext,
            dossierService,
            limits,
            NullLogger<CharacterDossiersGenerator>.Instance,
            extractionModelClient,
            new CharacterExtractionPromptBuilder(),
            patchClient,
            new DossierPatchPromptBuilder(),
            ApprovingReviewerClient(),
            new DossierConsistencyReviewerPromptBuilder(),
            NewIdentityResolverClient(),
            NewVectorSearchTool());

        await generator.GenerateAsync();

        Assert.Equal(2, patchClient.CallCount);
        Assert.All(patchClient.Requests, request =>
        {
            using var json = JsonDocument.Parse(request.UserPrompt);
            Assert.Single(json.RootElement.GetProperty("candidates").EnumerateArray());
        });
    }

    [Fact]
    public async Task GenerateDossiers_AppliesAliasPatchFromCandidateEvidence()
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
            Gender: "unknown"));

        var repository = new MarkdownDocumentRepository();
        var document = repository.LoadFromMarkdown("Johnny stood tall.");
        var documentContext = new DocumentContext(document, dossierService);
        var limits = new CharacterBibleExtractionLimits { MaxParagraphsPerBatch = 256, MaxBatchBytes = 1024 * 128 };
        var extractionModelClient = new ScriptedCharacterExtractionModelClient(
            Response(Character(
                "John",
                Alias("Johnny", "Johnny stood tall."))));
        var patchClient = new ScriptedDossierPatchProposalModelClient(
            ReadyPatch(aliasesToAdd: ["Johnny"]));

        var generator = new CharacterDossiersGenerator(
            documentContext,
            dossierService,
            limits,
            NullLogger<CharacterDossiersGenerator>.Instance,
            extractionModelClient,
            new CharacterExtractionPromptBuilder(),
            patchClient,
            new DossierPatchPromptBuilder(),
            ApprovingReviewerClient(),
            new DossierConsistencyReviewerPromptBuilder(),
            NewIdentityResolverClient(),
            NewVectorSearchTool());

        var dossiers = await generator.GenerateAsync();

        var dossier = Assert.Single(dossiers.Characters);
        Assert.Contains("Johnny", dossier.Aliases, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("Johnny stood tall.", dossier.AliasExamples["Johnny"]);
        Assert.Equal(1, patchClient.CallCount);
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
            new CharacterExtractionPromptBuilder(),
            NoopPatchClient(),
            new DossierPatchPromptBuilder(),
            ApprovingReviewerClient(),
            new DossierConsistencyReviewerPromptBuilder(),
            NewIdentityResolverClient(),
            NewVectorSearchTool());

        await generator.GenerateAsync();

        Assert.NotNull(extractionModelClient.LastRequest);
        Assert.Contains("Pronouns are NEVER aliases", extractionModelClient.LastRequest!.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("он", extractionModelClient.LastRequest.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("она", extractionModelClient.LastRequest.SystemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateDossiers_PromptRequiresPointerBackedEvidence()
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
            new CharacterExtractionPromptBuilder(),
            NoopPatchClient(),
            new DossierPatchPromptBuilder(),
            ApprovingReviewerClient(),
            new DossierConsistencyReviewerPromptBuilder(),
            NewIdentityResolverClient(),
            NewVectorSearchTool());

        await generator.GenerateAsync();

        Assert.NotNull(extractionModelClient.LastRequest);
        var systemPrompt = extractionModelClient.LastRequest!.SystemPrompt;
        Assert.Contains("Every character candidate must have pointers", systemPrompt, StringComparison.Ordinal);
        Assert.Contains("Preserve pointers exactly as provided", systemPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("statusAndCompetence", systemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateDossiers_PromptForbidsProfileWriting()
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
            new CharacterExtractionPromptBuilder(),
            NoopPatchClient(),
            new DossierPatchPromptBuilder(),
            ApprovingReviewerClient(),
            new DossierConsistencyReviewerPromptBuilder(),
            NewIdentityResolverClient(),
            NewVectorSearchTool());

        await generator.GenerateAsync();

        Assert.NotNull(extractionModelClient.LastRequest);
        var systemPrompt = extractionModelClient.LastRequest!.SystemPrompt;
        Assert.Contains("Do not write character profiles", systemPrompt, StringComparison.Ordinal);
        Assert.Contains("Do not decide whether a character is existing or new", systemPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Profile Rules", systemPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Language: RUSSIAN", systemPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("statusAndCompetence", systemPrompt, StringComparison.Ordinal);
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
            Response(Character(
                "John",
                Alias("Johnny", "Johnny laughed."))));

        var generator = new CharacterDossiersGenerator(
            documentContext,
            dossierService,
            limits,
            NullLogger<CharacterDossiersGenerator>.Instance,
            extractionModelClient,
            new CharacterExtractionPromptBuilder(),
            NoopPatchClient(),
            new DossierPatchPromptBuilder(),
            ApprovingReviewerClient(),
            new DossierConsistencyReviewerPromptBuilder(),
            NewIdentityResolverClient(),
            NewVectorSearchTool());

        var evidence = new[] { new AiTextEditor.Core.Model.EvidenceItem("1.p1", "Johnny laughed.", null) };
        await generator.UpdateFromEvidenceBatchAsync(evidence);

        var updated = dossierService.TryGetDossier("c1");
        Assert.NotNull(updated);
        Assert.Equal("Johnny waved.", updated!.AliasExamples["Johnny"]);
    }

    [Fact]
    public async Task PossessiveAlias_KeepsOnlyObservedForm()
    {
        var dossierService = new CharacterDossierService();
        var repository = new MarkdownDocumentRepository();
        var document = repository.LoadFromMarkdown("John's hat was on the table.");
        var documentContext = new DocumentContext(document, dossierService);
        var limits = new CharacterBibleExtractionLimits { MaxParagraphsPerBatch = 256, MaxBatchBytes = 1024 * 128 };

        var extractionModelClient = new ScriptedCharacterExtractionModelClient(
            Response(Character(
                "John",
                Alias("John's", "John's hat was on the table."))));

        var generator = new CharacterDossiersGenerator(
            documentContext,
            dossierService,
            limits,
            NullLogger<CharacterDossiersGenerator>.Instance,
            extractionModelClient,
            new CharacterExtractionPromptBuilder(),
            NoopPatchClient(),
            new DossierPatchPromptBuilder(),
            ApprovingReviewerClient(),
            new DossierConsistencyReviewerPromptBuilder(),
            NewIdentityResolverClient(),
            NewVectorSearchTool());

        var evidence = new[] { new AiTextEditor.Core.Model.EvidenceItem("p1", "John's hat was on the table.", null) };
        var dossiers = await generator.UpdateFromEvidenceBatchAsync(evidence);

        Assert.Single(dossiers.Characters);
        var character = dossiers.Characters[0];
        Assert.Contains("John's", character.AliasExamples.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("John", character.AliasExamples.Keys, StringComparer.OrdinalIgnoreCase);
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
            new CharacterExtractionPromptBuilder(),
            NoopPatchClient(),
            new DossierPatchPromptBuilder(),
            ApprovingReviewerClient(),
            new DossierConsistencyReviewerPromptBuilder(),
            NewIdentityResolverClient(),
            NewVectorSearchTool());

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
            new CharacterExtractionPromptBuilder(),
            NoopPatchClient(),
            new DossierPatchPromptBuilder(),
            ApprovingReviewerClient(),
            new DossierConsistencyReviewerPromptBuilder(),
            NewIdentityResolverClient(),
            NewVectorSearchTool());
        var runner = new CharacterBibleWorkflowRunner(generator, NullLoggerFactory.Instance);

        await runner.RunAsync(new CharacterBibleWorkflowInput());

        return extractionModelClient;
    }

    private static CharacterExtractionResponse Response(params ExtractedLocalCharacter[] characters)
        => new(characters);

    private static ExtractedLocalCharacter Character(
        string canonicalName,
        params string[] aliases)
        => new(
            canonicalName,
            "unknown",
            aliases,
            ["p1"]);

    private static string Alias(string form, string excerpt) => form;

    private static IDossierPatchProposalModelClient NoopPatchClient() => new NoopDossierPatchProposalModelClient();

    private static IDossierConsistencyReviewerModelClient ApprovingReviewerClient() => new ApprovingDossierConsistencyReviewerModelClient();

    private static ICharacterIdentityResolutionModelClient NewIdentityResolverClient()
        => new SearchBackedIdentityResolutionModelClient();

    private static ICharacterVectorSearchTool NewVectorSearchTool()
        => new TestCharacterVectorSearchTool();

    private static DossierPatchProposal ReadyPatch(
        IReadOnlyList<string>? aliasesToAdd = null,
        string? appearance = null,
        string? statusAndCompetence = null,
        string? psychologicalProfile = null,
        string? speechAndCommunication = null)
    {
        return new DossierPatchProposal
        {
            Status = "ready",
            AliasesToAdd = aliasesToAdd?.ToList() ?? [],
            ProfilePatch = new DossierProfilePatch(
                appearance,
                statusAndCompetence,
                psychologicalProfile,
                speechAndCommunication,
                [new DossierPatchEvidence("p1", "John stood tall and answered briefly.")]),
            Reason = "Profile evidence found."
        };
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

                var characters = new List<ExtractedLocalCharacter>();
                foreach (var paragraph in paragraphs.EnumerateArray())
                {
                    if (!paragraph.TryGetProperty("text", out var textElement) || textElement.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var text = textElement.GetString() ?? string.Empty;
                    var pointer = paragraph.TryGetProperty("pointer", out var pointerElement)
                        ? pointerElement.GetString() ?? string.Empty
                        : string.Empty;
                    var name = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                   .FirstOrDefault();
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    characters.Add(new ExtractedLocalCharacter(
                        name,
                        "unknown",
                        [],
                        [pointer]));
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
            return Task.FromResult(AlignPointers(response, request));
        }

        private static CharacterExtractionResponse AlignPointers(
            CharacterExtractionResponse response,
            CharacterExtractionModelRequest request)
        {
            var promptPointers = ReadPromptPointers(request);
            if (promptPointers.Length == 0)
            {
                return response;
            }

            var promptPointer = promptPointers[0];
            var promptPointerSet = promptPointers.ToHashSet(StringComparer.Ordinal);
            return new CharacterExtractionResponse(
                response.Characters
                    .Select(character => character.Pointers?.Any(pointer => promptPointerSet.Contains(pointer)) == true
                        ? character
                        : character with { Pointers = [promptPointer] })
                    .ToArray());
        }
    }

    private sealed class FailingCharacterExtractionModelClient(string message) : ICharacterExtractionModelClient
    {
        public Task<CharacterExtractionResponse> ExtractCharactersAsync(
            CharacterExtractionModelRequest request,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException(message);
    }

    private sealed class NoopDossierPatchProposalModelClient : IDossierPatchProposalModelClient
    {
        public Task<DossierPatchProposal> ProposePatchAsync(
            DossierPatchProposalModelRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new DossierPatchProposal
            {
                Status = "no_useful_changes",
                AliasesToAdd = [],
                ProfilePatch = null,
                Reason = "No test patch."
            });
        }
    }

    private sealed class ScriptedDossierPatchProposalModelClient(params DossierPatchProposal[] proposals)
        : IDossierPatchProposalModelClient
    {
        private readonly Queue<DossierPatchProposal> proposals = new(proposals);

        public int CallCount { get; private set; }

        public List<DossierPatchProposalModelRequest> Requests { get; } = [];

        public Task<DossierPatchProposal> ProposePatchAsync(
            DossierPatchProposalModelRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Requests.Add(request);
            return Task.FromResult(proposals.Count > 0
                ? proposals.Dequeue()
                : new DossierPatchProposal
                {
                    Status = "no_useful_changes",
                    AliasesToAdd = [],
                    ProfilePatch = null,
                    Reason = "No scripted patch."
                });
        }
    }

    private sealed class ApprovingDossierConsistencyReviewerModelClient : IDossierConsistencyReviewerModelClient
    {
        public Task<DossierReviewResult> ReviewAsync(
            DossierReviewModelRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new DossierReviewResult
            {
                Verdict = "approved",
                Issues = []
            });
        }
    }

    private sealed class SearchBackedIdentityResolutionModelClient : ICharacterIdentityResolutionModelClient
    {
        public async Task<CharacterIdentityResolutionResponse> ResolveAsync(
            CharacterIdentityResolutionModelRequest request,
            CancellationToken cancellationToken = default)
        {
            using var json = JsonDocument.Parse(request.UserPrompt);
            var candidate = json.RootElement.GetProperty("candidate");
            var name = candidate.GetProperty("name").GetString() ?? string.Empty;
            var aliases = candidate.GetProperty("aliases")
                .EnumerateArray()
                .Select(alias => alias.GetString())
                .Where(alias => !string.IsNullOrWhiteSpace(alias));
            var query = string.Join(' ', new[] { name }.Concat(aliases!));
            var hits = await request.SearchTool.SearchCharactersAsync(query, 5, cancellationToken);
            return hits.Count switch
            {
                0 => new CharacterIdentityResolutionResponse(CharacterIdentityDecision.New, Reason: "No test search hit."),
                1 => new CharacterIdentityResolutionResponse(CharacterIdentityDecision.Existing, hits[0].EntryId, Reason: "Matched by test search."),
                _ => new CharacterIdentityResolutionResponse(
                    CharacterIdentityDecision.Ambiguous,
                    EntryIds: hits.Select(hit => hit.EntryId).ToArray(),
                    Reason: "Multiple test search hits.")
            };
        }
    }

    private sealed class TestCharacterVectorSearchTool : ICharacterVectorSearchTool
    {
        public Task<IReadOnlyList<CharacterVectorSearchHit>> SearchAsync(
            CharacterDossiers dossiers,
            string query,
            int limit,
            CancellationToken cancellationToken)
        {
            var normalizedQuery = query.Trim();
            if (string.IsNullOrWhiteSpace(normalizedQuery) || limit <= 0)
            {
                return Task.FromResult<IReadOnlyList<CharacterVectorSearchHit>>([]);
            }

            var hits = dossiers.Characters
                .Where(dossier =>
                    string.Equals(dossier.Name, normalizedQuery, StringComparison.OrdinalIgnoreCase) ||
                    dossier.Aliases.Any(alias => normalizedQuery.Contains(alias, StringComparison.OrdinalIgnoreCase)) ||
                    normalizedQuery.Contains(dossier.Name, StringComparison.OrdinalIgnoreCase))
                .Select(dossier => new CharacterVectorSearchHit(
                    new CharacterVectorSearchCard(
                        dossier.CharacterId,
                        dossier.Name,
                        dossier.Gender,
                        dossier.Aliases,
                        string.Empty),
                    1d))
                .Take(limit)
                .ToArray();

            return Task.FromResult<IReadOnlyList<CharacterVectorSearchHit>>(hits);
        }
    }
}



