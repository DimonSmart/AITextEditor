using System.Text;
using System.Text.Json;
using AiTextEditor.Core.Services;
using AiTextEditor.Core.Model;
using AiTextEditor.Core.Interfaces;
using AiTextEditor.Agent;
using AiTextEditor.Agent.CharacterBible;
using AiTextEditor.Agent.CharacterBible.Diagnostics;
using AiTextEditor.Agent.CharacterBible.Extraction;
using AiTextEditor.Agent.CharacterBible.Normalization;
using AiTextEditor.Agent.CharacterBible.Patching;
using AiTextEditor.Agent.CharacterBible.Resolution;
using AiTextEditor.Agent.CharacterBible.VectorSearch;
using AiTextEditor.Web.Services;
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

        Assert.Contains("Pronouns are NEVER observedNameForms", systemPrompt, StringComparison.Ordinal);
        Assert.Contains("exact observed character name forms", systemPrompt, StringComparison.Ordinal);
        Assert.Contains("Do not return excerpts", systemPrompt, StringComparison.Ordinal);
        Assert.Contains("Do not return observed-name-form-level evidence", systemPrompt, StringComparison.Ordinal);
        Assert.Contains("Do not return character-level evidence", systemPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("character.profile", systemPrompt, StringComparison.Ordinal);

        using var json = JsonDocument.Parse(userPrompt);
        Assert.False(json.RootElement.TryGetProperty("task", out _));
        var paragraphs = json.RootElement.GetProperty("paragraphs").EnumerateArray().ToArray();
        Assert.Equal("p1", paragraphs[0].GetProperty("pointer").GetString());
        Assert.Equal("Анна вошла.", paragraphs[0].GetProperty("text").GetString());
        Assert.Equal("p2", paragraphs[1].GetProperty("pointer").GetString());
        Assert.Equal("Борис ответил.", paragraphs[1].GetProperty("text").GetString());
    }

    [Fact]
    public void BuildObservedNameFormExample_ReturnsShortContextContainingExactForm()
    {
        var paragraph = "Незнайка долго спорил с профессором Звездочкиным, потому что не понимал, как устроен космический аппарат.";
        var evidence = new CharacterBibleCandidateEvidence("p1", paragraph);

        var example = CharacterBibleExtractionMapper.BuildObservedNameFormExample(
            "профессором Звездочкиным",
            [evidence]);

        Assert.Contains("профессором Звездочкиным", example, StringComparison.Ordinal);
        Assert.True(example.Length < paragraph.Length);
        Assert.True(example.Length <= 160);
        Assert.Equal(paragraph, evidence.Excerpt);
    }

    [Fact]
    public void BuildObservedNameFormExample_HandlesStartEndAndRepeatedForm()
    {
        var start = CharacterBibleExtractionMapper.BuildObservedNameFormExample(
            "Пончик",
            [new CharacterBibleCandidateEvidence("p1", "Пончик быстро вошёл в комнату и сразу начал спорить с Незнайкой.")]);
        var end = CharacterBibleExtractionMapper.BuildObservedNameFormExample(
            "Пончика",
            [new CharacterBibleCandidateEvidence("p1", "Незнайка долго искал в толпе именно Пончика")]);
        var repeated = CharacterBibleExtractionMapper.BuildObservedNameFormExample(
            "Пончик",
            [new CharacterBibleCandidateEvidence("p1", "Пончик сначала молчал. Потом Пончик громко рассмеялся.")]);

        Assert.StartsWith("Пончик", start, StringComparison.Ordinal);
        Assert.EndsWith("Пончика", end, StringComparison.Ordinal);
        Assert.StartsWith("Пончик", repeated, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateDossiers_NormalizerUpdatesCanonicalNameForNewCharacter()
    {
        var dossierService = new CharacterDossierService();
        var repository = new MarkdownDocumentRepository();
        var document = repository.LoadFromMarkdown("Незнайка подошёл к профессору Звездочкину. Потом спросил профессора Звездочкина.");
        var documentContext = new DocumentContext(document, dossierService);
        var extractionModelClient = new ScriptedCharacterExtractionModelClient(
            Response(Character(
                "профессора Звездочкина",
                NameForm("профессора Звездочкина", "Потом спросил профессора Звездочкина."),
                NameForm("профессору Звездочкину", "Незнайка подошёл к профессору Звездочкину."))));
        var normalizer = new ScriptedCanonicalNameNormalizationModelClient(
            new CharacterCanonicalNameNormalizationResponse(
                "normalized",
                "профессор Звездочкин",
                "Observed forms support the nominative base form."));

        var generator = CreateGenerator(documentContext, dossierService, extractionModelClient, normalizer);

        var dossier = Assert.Single((await generator.GenerateAsync()).Characters);

        Assert.Equal("профессор Звездочкин", dossier.Name);
        Assert.Equal(1, normalizer.CallCount);
    }

    [Fact]
    public async Task GenerateDossiers_NormalizerInsufficientEvidencePreservesCurrentName()
    {
        var dossierService = new CharacterDossierService();
        var repository = new MarkdownDocumentRepository();
        var document = repository.LoadFromMarkdown("Пончика позвали первым.");
        var documentContext = new DocumentContext(document, dossierService);
        var extractionModelClient = new ScriptedCharacterExtractionModelClient(
            Response(Character("Пончика", NameForm("Пончика", "Пончика позвали первым."))));
        var normalizer = new ScriptedCanonicalNameNormalizationModelClient(
            new CharacterCanonicalNameNormalizationResponse(
                "insufficient_evidence",
                null,
                "No safe base form."));

        var generator = CreateGenerator(documentContext, dossierService, extractionModelClient, normalizer);

        var dossier = Assert.Single((await generator.GenerateAsync()).Characters);

        Assert.Equal("Пончика", dossier.Name);
        Assert.Equal(1, normalizer.CallCount);
    }

    [Fact]
    public async Task GenerateDossiers_NormalizerIsNotCalledForUnchangedExistingCharacter()
    {
        var dossierService = new CharacterDossierService();
        dossierService.UpsertDossier(new CharacterDossier(
            1,
            "John",
            ["John"],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["John"] = "John arrived."
            }));
        var repository = new MarkdownDocumentRepository();
        var document = repository.LoadFromMarkdown("John arrived.");
        var documentContext = new DocumentContext(document, dossierService);
        var extractionModelClient = new ScriptedCharacterExtractionModelClient(
            Response(Character("John", NameForm("John", "John arrived."))));
        var normalizer = new ScriptedCanonicalNameNormalizationModelClient();

        var generator = CreateGenerator(documentContext, dossierService, extractionModelClient, normalizer);

        var dossier = Assert.Single((await generator.GenerateAsync()).Characters);

        Assert.Equal("John", dossier.Name);
        Assert.Equal(0, normalizer.CallCount);
    }

    [Fact]
    public async Task GenerateDossiers_NormalizerIsCalledWhenObservedNameFormsAreMerged()
    {
        var dossierService = new CharacterDossierService();
        dossierService.UpsertDossier(new CharacterDossier(
            1,
            "John",
            ["John"],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["John"] = "John arrived."
            }));
        var repository = new MarkdownDocumentRepository();
        var document = repository.LoadFromMarkdown("Johnny laughed.");
        var documentContext = new DocumentContext(document, dossierService);
        var extractionModelClient = new ScriptedCharacterExtractionModelClient(
            Response(Character("John", NameForm("Johnny", "Johnny laughed."))));
        var normalizer = new ScriptedCanonicalNameNormalizationModelClient(
            new CharacterCanonicalNameNormalizationResponse(
                "insufficient_evidence",
                null,
                "No rename."));

        var generator = CreateGenerator(documentContext, dossierService, extractionModelClient, normalizer);

        var dossier = Assert.Single((await generator.GenerateAsync()).Characters);

        Assert.Contains("Johnny", dossier.ObservedNameForms, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(1, normalizer.CallCount);
    }

    [Fact]
    public async Task GenerateDossiers_InvalidNormalizerResponseDoesNotModifyDossier()
    {
        var dossierService = new CharacterDossierService();
        var repository = new MarkdownDocumentRepository();
        var document = repository.LoadFromMarkdown("Пончика позвали первым.");
        var documentContext = new DocumentContext(document, dossierService);
        var extractionModelClient = new ScriptedCharacterExtractionModelClient(
            Response(Character("Пончика", NameForm("Пончика", "Пончика позвали первым."))));
        var normalizer = new ScriptedCanonicalNameNormalizationModelClient(
            new CharacterCanonicalNameNormalizationResponse(
                "unsupported",
                "Пончик",
                "Invalid test response."));

        var generator = CreateGenerator(documentContext, dossierService, extractionModelClient, normalizer);

        var dossier = Assert.Single((await generator.GenerateAsync()).Characters);

        Assert.Equal("Пончика", dossier.Name);
        Assert.Equal(1, normalizer.CallCount);
    }

    [Fact]
    public void CharacterProfileUpdatePromptBuilder_LoadsPromptAndBuildsUserPromptJson()
    {
        var builder = new CharacterProfileUpdatePromptBuilder("Russian");
        var candidate = new CharacterBibleCharacterCandidate(
            "John",
            "unknown",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Johnny"] = "Johnny entered."
            },
            [new CharacterBibleCandidateEvidence("p1", "Johnny entered.")]);
        var dossier = new AiTextEditor.Core.Model.CharacterDossier(
            1,
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
        var userPrompt = builder.BuildUserPrompt([patchCandidate], dossier);

        Assert.Contains("If newEvidence does not change the profile, call no tools", systemPrompt, StringComparison.Ordinal);
        Assert.Contains("If newEvidence only confirms the current profile, call no tools", systemPrompt, StringComparison.Ordinal);
        Assert.Contains("If newEvidence only describes a one-time action", systemPrompt, StringComparison.Ordinal);
        Assert.Contains("Each tool call must contain only field and value", systemPrompt, StringComparison.Ordinal);
        Assert.Contains("replace_profile_field.value is the full new value", systemPrompt, StringComparison.Ordinal);
        Assert.Contains("Do not return reasons", systemPrompt, StringComparison.Ordinal);
        Assert.Contains("Do not return evidence pointers", systemPrompt, StringComparison.Ordinal);
        Assert.Contains("Do not return status", systemPrompt, StringComparison.Ordinal);
        Assert.Contains("Target length: 500 characters or less", systemPrompt, StringComparison.Ordinal);
        Assert.Contains("Choose the field by semantic type", systemPrompt, StringComparison.Ordinal);
        Assert.Contains("outputLanguage", systemPrompt, StringComparison.Ordinal);
        Assert.Contains("profile field values", systemPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("completed", systemPrompt, StringComparison.Ordinal);

        using var json = JsonDocument.Parse(userPrompt);
        Assert.Equal("John", json.RootElement.GetProperty("target").GetProperty("name").GetString());
        Assert.Equal("Russian", json.RootElement.GetProperty("outputLanguage").GetString());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("currentProfile").GetProperty("appearance").ValueKind);
        var evidence = json.RootElement.GetProperty("newEvidence").EnumerateArray().Single();
        Assert.Equal("p1", evidence.GetProperty("pointer").GetString());
        Assert.Equal("Johnny entered and answered briefly.", evidence.GetProperty("focusedText").GetString());
        Assert.Equal("Mary watched.", evidence.GetProperty("contextAfter").GetString());
        Assert.False(evidence.TryGetProperty("text", out _));
        Assert.DoesNotContain("Anchor excerpt", userPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Current paragraph", userPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Focused text", userPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("candidateId", userPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("characterId", userPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("identityDecision", userPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("candidateIds", userPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("evidenceContexts", userPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("observedNameForms", userPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("additions", userPrompt, StringComparison.Ordinal);
        Assert.False(json.RootElement.TryGetProperty("proposal", out _));
    }

    [Fact]
    public void CharacterProfileUpdatePromptBuilder_DefaultLanguageIsRussian()
    {
        var builder = new CharacterProfileUpdatePromptBuilder();
        var candidate = new CharacterBibleCharacterCandidate(
            "John",
            "unknown",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            [new CharacterBibleCandidateEvidence("p1", "John entered.")]);
        var dossier = new AiTextEditor.Core.Model.CharacterDossier(
            1,
            "John",
            ["John"],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["John"] = "John entered."
            },
            "unknown");
        var patchCandidate = new CharacterBibleDossierPatchCandidate(
            candidate,
            [
                new CharacterBibleEvidenceContext(
                    "p1",
                    "John entered.",
                    "John entered.",
                    "John entered.",
                    [])
            ]);

        var userPrompt = builder.BuildUserPrompt([patchCandidate], dossier);

        using var json = JsonDocument.Parse(userPrompt);
        Assert.Equal("Russian", json.RootElement.GetProperty("outputLanguage").GetString());
    }

    [Fact]
    public void ProgramSettingsClone_PreservesCharacterBibleDossierLanguage()
    {
        var settings = new ProgramSettings
        {
            CharacterBibleDossierLanguage = "English"
        };

        var clone = settings.Clone();

        Assert.Equal("English", clone.CharacterBibleDossierLanguage);
    }

    [Fact]
    public void ProgramSettingsValidation_RejectsEmptyCharacterBibleDossierLanguage()
    {
        var settings = new ProgramSettings
        {
            CharacterBibleDossierLanguage = string.Empty
        };

        var errors = ProgramSettingsValidation.ValidateForSave(settings);

        Assert.Contains(errors, error => error.Contains("dossier language", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CharacterIdentityResolutionPromptBuilder_IncludesNearNameRule()
    {
        var builder = new CharacterIdentityResolutionPromptBuilder();

        var systemPrompt = builder.BuildSystemPrompt();

        Assert.Contains("Name similarity is only a retrieval hint, not identity evidence.", systemPrompt, StringComparison.Ordinal);
        Assert.Contains("or one name contains the other as a substring", systemPrompt, StringComparison.Ordinal);
        Assert.Contains("do not merge by name similarity, retrieval rank", systemPrompt, StringComparison.Ordinal);
        Assert.Contains("Check local textual evidence", systemPrompt, StringComparison.Ordinal);
        Assert.Contains("prefer new or ambiguous", systemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public void CharacterIdentityResolutionPromptBuilder_MaterializesAllCandidatePointers()
    {
        var builder = new CharacterIdentityResolutionPromptBuilder();
        var candidate = new CharacterBibleCharacterCandidate(
            "Знайка",
            "unknown",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            [
                new CharacterBibleCandidateEvidence("1.1.1.p8", "Знайка ответил третьим."),
                new CharacterBibleCandidateEvidence("1.1.1.p5", "Знайка вошел."),
                new CharacterBibleCandidateEvidence("1.1.1.p6", "Знайка сказал.")
            ]);

        var input = builder.BuildPromptInput(candidate);

        Assert.Equal(
            ["1.1.1.p5", "1.1.1.p6", "1.1.1.p8"],
            input.Candidate.Evidence.Select(evidence => evidence.Pointer).ToArray());
        Assert.Equal("Знайка вошел.", input.Candidate.Evidence[0].Text);
        Assert.Equal("Знайка сказал.", input.Candidate.Evidence[1].Text);
        Assert.Equal("Знайка ответил третьим.", input.Candidate.Evidence[2].Text);
    }

    [Fact]
    public void CharacterIdentityResolutionPromptBuilder_DoesNotSerializeCandidatePointers()
    {
        var builder = new CharacterIdentityResolutionPromptBuilder();
        var candidate = new CharacterBibleCharacterCandidate(
            "Знайка",
            "unknown",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            [new CharacterBibleCandidateEvidence("1.1.1.p5", "Знайка вошел.")]);

        var userPrompt = builder.BuildUserPrompt(candidate);

        Assert.DoesNotContain("\"candidateId\"", userPrompt, StringComparison.Ordinal);
        Assert.Contains("\"evidence\":[", userPrompt, StringComparison.Ordinal);
        Assert.Contains("\"pointer\":\"1.1.1.p5\"", userPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("\"pointers\"", userPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("\"paragraphs\"", userPrompt, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(userPrompt);
        Assert.False(json.RootElement.GetProperty("candidate").TryGetProperty("pointers", out _));
        Assert.Equal(
            "1.1.1.p5",
            json.RootElement.GetProperty("candidate").GetProperty("evidence").EnumerateArray().Single().GetProperty("pointer").GetString());
    }

    [Fact]
    public void CharacterIdentityResolutionPromptBuilder_FailsWhenPointerCannotBeMaterialized()
    {
        var builder = new CharacterIdentityResolutionPromptBuilder();
        var candidate = new CharacterBibleCharacterCandidate(
            "Знайка",
            "unknown",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            [
                new CharacterBibleCandidateEvidence("1.1.1.p5", "Знайка вошел."),
                new CharacterBibleCandidateEvidence("1.1.1.p404", " ")
            ]);

        var exception = Assert.Throws<InvalidOperationException>(() => builder.BuildPromptInput(candidate));
        Assert.Contains("1.1.1.p404", exception.Message, StringComparison.Ordinal);
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
            new CharacterProfileUpdatePromptBuilder(),
            NewIdentityResolverClient(),
            NoopCanonicalNameNormalizationClient(),
            new CharacterCanonicalNameNormalizationPromptBuilder(),
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
            new CharacterProfileUpdatePromptBuilder(),
            NewIdentityResolverClient(),
            NoopCanonicalNameNormalizationClient(),
            new CharacterCanonicalNameNormalizationPromptBuilder(),
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
            CharacterId: 1,
            Name: "John",
            ObservedNameForms: ["John"],
            ObservedNameFormExamples: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
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
                NameForm("John", "John and Mary met Bob."))));

        var generator = new CharacterDossiersGenerator(
            documentContext,
            dossierService,
            limits,
            NullLogger<CharacterDossiersGenerator>.Instance,
            extractionModelClient,
            new CharacterExtractionPromptBuilder(),
            NoopPatchClient(),
            new CharacterProfileUpdatePromptBuilder(),
            NewIdentityResolverClient(),
            NoopCanonicalNameNormalizationClient(),
            new CharacterCanonicalNameNormalizationPromptBuilder(),
            NewVectorSearchTool());

        var dossiers = await generator.GenerateAsync();

        var dossier = Assert.Single(dossiers.Characters);
        Assert.Null(dossier.Profile!.Appearance);
        Assert.Null(dossier.Profile.StatusAndCompetence);
        Assert.Equal("Manual psychological profile.", dossier.Profile.PsychologicalProfile);
        Assert.Null(dossier.Profile.SpeechAndCommunication);
        Assert.Equal("John arrived.", dossier.ObservedNameFormExamples["John"]);
    }

    [Fact]
    public async Task GenerateDossiers_NoToolCallKeepsProfileUnchanged()
    {
        var dossierService = new CharacterDossierService();
        dossierService.UpsertDossier(new AiTextEditor.Core.Model.CharacterDossier(
            CharacterId: 1,
            Name: "John",
            ObservedNameForms: ["John"],
            ObservedNameFormExamples: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            Gender: "unknown",
            Profile: new AiTextEditor.Core.Model.CharacterProfile(
                PsychologicalProfile: "Проявляет храбрость.")));

        var repository = new MarkdownDocumentRepository();
        var document = repository.LoadFromMarkdown("John вошёл.");
        var documentContext = new DocumentContext(document, dossierService);
        var limits = new CharacterBibleExtractionLimits { MaxParagraphsPerBatch = 256, MaxBatchBytes = 1024 * 128 };
        var extractionModelClient = new ScriptedCharacterExtractionModelClient(
            Response(Character("John", NameForm("John", "John вошёл."))));
        var patchClient = new ScriptedCharacterProfileUpdateModelClient();

        var generator = new CharacterDossiersGenerator(
            documentContext,
            dossierService,
            limits,
            NullLogger<CharacterDossiersGenerator>.Instance,
            extractionModelClient,
            new CharacterExtractionPromptBuilder(),
            patchClient,
            new CharacterProfileUpdatePromptBuilder(),
            NewIdentityResolverClient(),
            NoopCanonicalNameNormalizationClient(),
            new CharacterCanonicalNameNormalizationPromptBuilder(),
            NewVectorSearchTool());

        var dossiers = await generator.GenerateAsync();

        Assert.Equal("Проявляет храбрость.", Assert.Single(dossiers.Characters).Profile!.PsychologicalProfile);
    }

    [Fact]
    public async Task GenerateDossiers_OneTimeActionDoesNotBecomeStableTrait()
    {
        var dossierService = new CharacterDossierService();
        var repository = new MarkdownDocumentRepository();
        var document = repository.LoadFromMarkdown("John один раз открыл дверь.");
        var documentContext = new DocumentContext(document, dossierService);
        var limits = new CharacterBibleExtractionLimits { MaxParagraphsPerBatch = 256, MaxBatchBytes = 1024 * 128 };
        var extractionModelClient = new ScriptedCharacterExtractionModelClient(
            Response(Character("John", NameForm("John", "John один раз открыл дверь."))));

        var generator = new CharacterDossiersGenerator(
            documentContext,
            dossierService,
            limits,
            NullLogger<CharacterDossiersGenerator>.Instance,
            extractionModelClient,
            new CharacterExtractionPromptBuilder(),
            NoopPatchClient(),
            new CharacterProfileUpdatePromptBuilder(),
            NewIdentityResolverClient(),
            NoopCanonicalNameNormalizationClient(),
            new CharacterCanonicalNameNormalizationPromptBuilder(),
            NewVectorSearchTool());

        var dossier = Assert.Single((await generator.GenerateAsync()).Characters);

        Assert.Equal(AiTextEditor.Core.Model.CharacterProfile.Empty, dossier.Profile);
    }

    [Fact]
    public async Task GenerateDossiers_LaterEvidenceCanReplacePsychologicalProfileThroughTool()
    {
        var currentProfile = new AiTextEditor.Core.Model.CharacterProfile(
            Appearance: "Старое описание внешности.",
            PsychologicalProfile: "Проявляет храбрость.");
        var proposedPsychologicalProfile = "Не обладает устойчивой храбростью: при реальной опасности склонен теряться.";
        var result = await GenerateWithProfileUpdateAsync(
            currentProfile,
            new ProfileFieldReplacement(
                CharacterBibleProfileField.PsychologicalProfile,
                proposedPsychologicalProfile));

        Assert.Equal("Старое описание внешности.", result.Dossier.Profile!.Appearance);
        Assert.Equal(proposedPsychologicalProfile, result.Dossier.Profile.PsychologicalProfile);
        Assert.DoesNotContain("Проявляет храбрость.", result.Dossier.Profile.PsychologicalProfile, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateDossiers_ProfileUpdateUsesFieldAndValueOnly()
    {
        var currentProfile = new AiTextEditor.Core.Model.CharacterProfile(
            Appearance: "Старое описание внешности.",
            PsychologicalProfile: "Проявляет храбрость.");
        var result = await GenerateWithProfileUpdateAsync(
            currentProfile,
            new ProfileFieldReplacement(
                CharacterBibleProfileField.PsychologicalProfile,
                "При реальной опасности склонен теряться."));

        Assert.Equal("При реальной опасности склонен теряться.", result.Dossier.Profile!.PsychologicalProfile);
    }

    [Fact]
    public async Task GenerateDossiers_AppliesProfileUpdateWhilePreservingUnaffectedSections()
    {
        var dossierService = new CharacterDossierService();
        dossierService.UpsertDossier(new AiTextEditor.Core.Model.CharacterDossier(
            CharacterId: 1,
            Name: "John",
            ObservedNameForms: ["John"],
            ObservedNameFormExamples: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
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
                NameForm("John", "John stood tall and answered briefly."))));
        var patchClient = new ScriptedCharacterProfileUpdateModelClient(
            ReadyPatch(
                appearance: "Высокая спокойная фигура.",
                statusAndCompetence: "Держится уверенно.",
                psychologicalProfile: "Manual psychological profile.",
                speechAndCommunication: "Отвечает кратко.",
                changedFields:
                [
                    CharacterBibleProfileField.Appearance,
                    CharacterBibleProfileField.StatusAndCompetence,
                    CharacterBibleProfileField.SpeechAndCommunication
                ]));

        var generator = new CharacterDossiersGenerator(
            documentContext,
            dossierService,
            limits,
            NullLogger<CharacterDossiersGenerator>.Instance,
            extractionModelClient,
            new CharacterExtractionPromptBuilder(),
            patchClient,
            new CharacterProfileUpdatePromptBuilder(),
            NewIdentityResolverClient(),
            NoopCanonicalNameNormalizationClient(),
            new CharacterCanonicalNameNormalizationPromptBuilder(),
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
    public async Task GenerateDossiers_LaterEvidenceReplacesPsychologicalProfile()
    {
        var dossierService = new CharacterDossierService();
        dossierService.UpsertDossier(new AiTextEditor.Core.Model.CharacterDossier(
            CharacterId: 1,
            Name: "John",
            ObservedNameForms: ["John"],
            ObservedNameFormExamples: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["John"] = "John arrived."
            },
            Gender: "unknown",
            Profile: new AiTextEditor.Core.Model.CharacterProfile(
                PsychologicalProfile: "Manual psychological profile.")));

        var repository = new MarkdownDocumentRepository();
        var document = repository.LoadFromMarkdown("Позже выяснилось: прежняя смелость John была случайным впечатлением, а при реальной опасности он растерялся.");
        var documentContext = new DocumentContext(document, dossierService);
        var limits = new CharacterBibleExtractionLimits { MaxParagraphsPerBatch = 256, MaxBatchBytes = 1024 * 128 };
        var extractionModelClient = new ScriptedCharacterExtractionModelClient(
            Response(Character(
                "John",
                NameForm("John", "Позже выяснилось: прежняя смелость John была случайным впечатлением, а при реальной опасности он растерялся."))));
        var patchClient = new ScriptedCharacterProfileUpdateModelClient(
            ReadyPatch(psychologicalProfile: "Не обладает устойчивой храбростью: может выглядеть смелым из-за обстоятельств, но при реальной опасности склонен теряться."));

        var generator = new CharacterDossiersGenerator(
            documentContext,
            dossierService,
            limits,
            NullLogger<CharacterDossiersGenerator>.Instance,
            extractionModelClient,
            new CharacterExtractionPromptBuilder(),
            patchClient,
            new CharacterProfileUpdatePromptBuilder(),
            NewIdentityResolverClient(),
            NoopCanonicalNameNormalizationClient(),
            new CharacterCanonicalNameNormalizationPromptBuilder(),
            NewVectorSearchTool());

        var dossiers = await generator.GenerateAsync();

        var dossier = Assert.Single(dossiers.Characters);
        Assert.Equal("Не обладает устойчивой храбростью: может выглядеть смелым из-за обстоятельств, но при реальной опасности склонен теряться.", dossier.Profile!.PsychologicalProfile);
    }

    [Fact]
    public async Task GenerateDossiers_ProfileUpdaterAppliesToolCallWithoutReviewer()
    {
        var dossierService = new CharacterDossierService();
        var repository = new MarkdownDocumentRepository();
        var document = repository.LoadFromMarkdown("John wore old clothes that smelled of naphthalene.");
        var documentContext = new DocumentContext(document, dossierService);
        var limits = new CharacterBibleExtractionLimits { MaxParagraphsPerBatch = 256, MaxBatchBytes = 1024 * 128 };
        var extractionModelClient = new ScriptedCharacterExtractionModelClient(
            Response(Character(
                "John",
                NameForm("John", "John wore old clothes that smelled of naphthalene."))));
        var patchClient = new ScriptedCharacterProfileUpdateModelClient(
            ReadyPatch(statusAndCompetence: "Пытался улучшить гардероб."));

        var generator = new CharacterDossiersGenerator(
            documentContext,
            dossierService,
            limits,
            NullLogger<CharacterDossiersGenerator>.Instance,
            extractionModelClient,
            new CharacterExtractionPromptBuilder(),
            patchClient,
            new CharacterProfileUpdatePromptBuilder(),
            NewIdentityResolverClient(),
            NoopCanonicalNameNormalizationClient(),
            new CharacterCanonicalNameNormalizationPromptBuilder(),
            NewVectorSearchTool());

        var dossiers = await generator.GenerateAsync();

        var dossier = Assert.Single(dossiers.Characters);
        Assert.Equal("Пытался улучшить гардероб.", dossier.Profile!.StatusAndCompetence);
    }

    [Fact]
    public async Task GenerateDossiers_ProfileUpdateValidationRejectLogsGroupDetails()
    {
        var logger = new CapturingCharacterBibleRunLogger();
        var dossierService = new CharacterDossierService();
        var repository = new MarkdownDocumentRepository();
        var document = repository.LoadFromMarkdown("John hid behind the chair.");
        var documentContext = new DocumentContext(document, dossierService);
        var limits = new CharacterBibleExtractionLimits { MaxParagraphsPerBatch = 256, MaxBatchBytes = 1024 * 128 };
        var extractionModelClient = new ScriptedCharacterExtractionModelClient(
            Response(Character(
                "John",
                NameForm("John", "John hid behind the chair."))));
        var patchClient = new ScriptedCharacterProfileUpdateModelClient(
            new ProfileFieldReplacement(
                CharacterBibleProfileField.PsychologicalProfile,
                "## Невалидное markdown-значение"));

        var generator = new CharacterDossiersGenerator(
            documentContext,
            dossierService,
            limits,
            NullLogger<CharacterDossiersGenerator>.Instance,
            extractionModelClient,
            new CharacterExtractionPromptBuilder(),
            patchClient,
            new CharacterProfileUpdatePromptBuilder(),
            NewIdentityResolverClient(),
            NoopCanonicalNameNormalizationClient(),
            new CharacterCanonicalNameNormalizationPromptBuilder(),
            NewVectorSearchTool());

        using (CharacterBibleRunLogScope.Push(logger))
        {
            await generator.GenerateAsync();
        }

        var rejectedToolCall = Assert.Single(logger.WarningMessages, message =>
            message.EventName == "profile.update.tool.rejected");
        Assert.Contains("field=\"PsychologicalProfile\"", rejectedToolCall.Message, StringComparison.Ordinal);
        Assert.Contains("rule=\"contains_prompt_artifact\"", rejectedToolCall.Message, StringComparison.Ordinal);
        Assert.Contains("evidencePointers=[\"p1\"]", rejectedToolCall.Message, StringComparison.Ordinal);

        var groupFailed = Assert.Single(logger.ErrorMessages, message =>
            message.EventName == "profile.update.group.failed");
        Assert.Contains("reason=\"tool validation failed\"", groupFailed.Message, StringComparison.Ordinal);
        Assert.Contains("applied=0", groupFailed.Message, StringComparison.Ordinal);
        Assert.Contains("rejected=1", groupFailed.Message, StringComparison.Ordinal);
        Assert.Contains("rejectedFields=[\"PsychologicalProfile\"]", groupFailed.Message, StringComparison.Ordinal);
        Assert.Contains("rejectedRules=[\"contains_prompt_artifact\"]", groupFailed.Message, StringComparison.Ordinal);
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
        var patchClient = new ScriptedCharacterProfileUpdateModelClient();

        var generator = new CharacterDossiersGenerator(
            documentContext,
            dossierService,
            limits,
            NullLogger<CharacterDossiersGenerator>.Instance,
            extractionModelClient,
            new CharacterExtractionPromptBuilder(),
            patchClient,
            new CharacterProfileUpdatePromptBuilder(),
            NewIdentityResolverClient(),
            NoopCanonicalNameNormalizationClient(),
            new CharacterCanonicalNameNormalizationPromptBuilder(),
            NewVectorSearchTool());

        await generator.GenerateAsync();

        var request = Assert.Single(patchClient.Requests);
        using var json = JsonDocument.Parse(request.UserPrompt);
        var evidence = json.RootElement.GetProperty("newEvidence").EnumerateArray().Single();
        Assert.Equal("p1", evidence.GetProperty("pointer").GetString());
        Assert.Equal("А Незнайка сказал:", evidence.GetProperty("focusedText").GetString());
        Assert.Equal("– Мы будем сегодня завтракать?", evidence.GetProperty("contextAfter").GetString());
        Assert.False(evidence.TryGetProperty("text", out _));
    }

    [Fact]
    public async Task GenerateDossiers_PatchPromptFocusesEvidenceAroundTargetObservedForm()
    {
        var dossierService = new CharacterDossierService();
        dossierService.UpsertDossier(new AiTextEditor.Core.Model.CharacterDossier(
            CharacterId: 1,
            Name: "Незнайка",
            ObservedNameForms: ["Незнайка"],
            ObservedNameFormExamples: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Незнайка"] = "Незнайка совершил путешествие."
            },
            Gender: "male"));
        dossierService.UpsertDossier(new AiTextEditor.Core.Model.CharacterDossier(
            CharacterId: 2,
            Name: "Кнопочка",
            ObservedNameForms: ["Кнопочка"],
            ObservedNameFormExamples: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Кнопочка"] = "Кнопочка путешествовала."
            },
            Gender: "female"));
        var repository = new MarkdownDocumentRepository();
        var document = repository.LoadFromMarkdown(
            "С тех пор как Незнайка совершил путешествие, о нём говорили все. Наслушавшись рассказов Незнайки, Кнопочки и Пачкули Пёстренького, многие коротышки тоже совершили поездку в Солнечный город.");
        var documentContext = new DocumentContext(document, dossierService);
        var limits = new CharacterBibleExtractionLimits { MaxParagraphsPerBatch = 256, MaxBatchBytes = 1024 * 128 };
        var extractionModelClient = new ScriptedCharacterExtractionModelClient(
            Response(
                new ExtractedLocalCharacter(
                    "Незнайка",
                    "male",
                    ["Незнайка"],
                    ["p1"]),
                new ExtractedLocalCharacter(
                    "Кнопочка",
                    "female",
                    ["Кнопочки"],
                    ["p1"])));
        var patchClient = new ScriptedCharacterProfileUpdateModelClient();
        var logger = new CapturingCharacterBibleRunLogger();

        var generator = new CharacterDossiersGenerator(
            documentContext,
            dossierService,
            limits,
            NullLogger<CharacterDossiersGenerator>.Instance,
            extractionModelClient,
            new CharacterExtractionPromptBuilder(),
            patchClient,
            new CharacterProfileUpdatePromptBuilder(),
            NewIdentityResolverClient(),
            NoopCanonicalNameNormalizationClient(),
            new CharacterCanonicalNameNormalizationPromptBuilder(),
            NewVectorSearchTool());

        using (CharacterBibleRunLogScope.Push(logger))
        {
            await generator.GenerateAsync();
        }

        var request = patchClient.Requests.Single(request => request.UserPrompt.Contains("\"name\":\"Кнопочка\"", StringComparison.Ordinal));
        using var json = JsonDocument.Parse(request.UserPrompt);
        var focusedText = json.RootElement
            .GetProperty("newEvidence")
            .EnumerateArray()
            .Single()
            .GetProperty("focusedText")
            .GetString();
        Assert.Contains("Кнопочки", focusedText, StringComparison.Ordinal);
        Assert.False(focusedText?.StartsWith("С тех пор как Незнайка", StringComparison.Ordinal));
        Assert.Contains(logger.DebugMessages, message =>
            message.EventName == "profile.evidence.focus"
            && message.Message.Contains("name=\"Кнопочка\"", StringComparison.Ordinal)
            && message.Message.Contains("containsObservedForm=true", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.WarningMessages, message =>
            message.EventName == "profile.evidence.focus.fallback"
            && message.Message.Contains("name=\"Кнопочка\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GenerateDossiers_PatchPromptLogsFallbackWhenObservedFormIsMissing()
    {
        var dossierService = new CharacterDossierService();
        var repository = new MarkdownDocumentRepository();
        var document = repository.LoadFromMarkdown("John entered the room.");
        var documentContext = new DocumentContext(document, dossierService);
        var limits = new CharacterBibleExtractionLimits { MaxParagraphsPerBatch = 256, MaxBatchBytes = 1024 * 128 };
        var extractionModelClient = new ScriptedCharacterExtractionModelClient(
            Response(new ExtractedLocalCharacter(
                "Johnny",
                "male",
                ["Johnny"],
                ["p1"])));
        var patchClient = new ScriptedCharacterProfileUpdateModelClient();
        var logger = new CapturingCharacterBibleRunLogger();

        var generator = new CharacterDossiersGenerator(
            documentContext,
            dossierService,
            limits,
            NullLogger<CharacterDossiersGenerator>.Instance,
            extractionModelClient,
            new CharacterExtractionPromptBuilder(),
            patchClient,
            new CharacterProfileUpdatePromptBuilder(),
            NewIdentityResolverClient(),
            NoopCanonicalNameNormalizationClient(),
            new CharacterCanonicalNameNormalizationPromptBuilder(),
            NewVectorSearchTool());

        using (CharacterBibleRunLogScope.Push(logger))
        {
            await generator.GenerateAsync();
        }

        var fallback = Assert.Single(logger.WarningMessages, message =>
            message.EventName == "profile.evidence.focus.fallback");
        Assert.Contains("name=\"Johnny\"", fallback.Message, StringComparison.Ordinal);
        Assert.Contains("pointer=\"p1\"", fallback.Message, StringComparison.Ordinal);
        Assert.Contains("observedForms=[\"Johnny\"]", fallback.Message, StringComparison.Ordinal);
        Assert.Contains(logger.DebugMessages, message =>
            message.EventName == "profile.evidence.focus"
            && message.Message.Contains("found=false", StringComparison.Ordinal)
            && message.Message.Contains("containsObservedForm=false", StringComparison.Ordinal));
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
        var patchClient = new ScriptedCharacterProfileUpdateModelClient();

        var generator = new CharacterDossiersGenerator(
            documentContext,
            dossierService,
            limits,
            NullLogger<CharacterDossiersGenerator>.Instance,
            extractionModelClient,
            new CharacterExtractionPromptBuilder(),
            patchClient,
            new CharacterProfileUpdatePromptBuilder(),
            NewIdentityResolverClient(),
            NoopCanonicalNameNormalizationClient(),
            new CharacterCanonicalNameNormalizationPromptBuilder(),
            NewVectorSearchTool());

        await generator.GenerateAsync();

        var request = Assert.Single(patchClient.Requests);
        using var json = JsonDocument.Parse(request.UserPrompt);
        var evidencePointers = json.RootElement.GetProperty("newEvidence")
            .EnumerateArray()
            .Select(evidence => evidence.GetProperty("pointer").GetString() ?? string.Empty)
            .OrderBy(pointer => pointer, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(["p1", "p2"], evidencePointers);
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
        var patchClient = new ScriptedCharacterProfileUpdateModelClient();

        var generator = new CharacterDossiersGenerator(
            documentContext,
            dossierService,
            limits,
            NullLogger<CharacterDossiersGenerator>.Instance,
            extractionModelClient,
            new CharacterExtractionPromptBuilder(),
            patchClient,
            new CharacterProfileUpdatePromptBuilder(),
            NewIdentityResolverClient(),
            NoopCanonicalNameNormalizationClient(),
            new CharacterCanonicalNameNormalizationPromptBuilder(),
            NewVectorSearchTool());

        await generator.GenerateAsync();

        Assert.Equal(2, patchClient.CallCount);
        Assert.All(patchClient.Requests, request =>
        {
            using var json = JsonDocument.Parse(request.UserPrompt);
            Assert.Single(json.RootElement.GetProperty("newEvidence").EnumerateArray());
        });
    }

    [Fact]
    public async Task GenerateDossiers_PatchPromptIncludesCurrentObservedNameForms()
    {
        var dossierService = new CharacterDossierService();
        dossierService.UpsertDossier(new AiTextEditor.Core.Model.CharacterDossier(
            CharacterId: 1,
            Name: "John",
            ObservedNameForms: ["John"],
            ObservedNameFormExamples: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
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
                NameForm("Johnny", "Johnny stood tall."))));
        var patchClient = new ScriptedCharacterProfileUpdateModelClient(
            ReadyPatch(statusAndCompetence: "Стоит прямо."));

        var generator = new CharacterDossiersGenerator(
            documentContext,
            dossierService,
            limits,
            NullLogger<CharacterDossiersGenerator>.Instance,
            extractionModelClient,
            new CharacterExtractionPromptBuilder(),
            patchClient,
            new CharacterProfileUpdatePromptBuilder(),
            NewIdentityResolverClient(),
            NoopCanonicalNameNormalizationClient(),
            new CharacterCanonicalNameNormalizationPromptBuilder(),
            NewVectorSearchTool());

        var dossiers = await generator.GenerateAsync();

        var dossier = Assert.Single(dossiers.Characters);
        Assert.Contains("Johnny", dossier.ObservedNameForms, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("Johnny stood tall.", dossier.ObservedNameFormExamples["Johnny"]);
        Assert.Equal(1, patchClient.CallCount);
        Assert.DoesNotContain("\"observedNameForms\"", patchClient.Requests.Single().UserPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateDossiers_PromptForbidsPronounObservedNameForms()
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
            new CharacterProfileUpdatePromptBuilder(),
            NewIdentityResolverClient(),
            NoopCanonicalNameNormalizationClient(),
            new CharacterCanonicalNameNormalizationPromptBuilder(),
            NewVectorSearchTool());

        await generator.GenerateAsync();

        Assert.NotNull(extractionModelClient.LastRequest);
        Assert.Contains("Pronouns are NEVER observedNameForms", extractionModelClient.LastRequest!.SystemPrompt, StringComparison.Ordinal);
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
            new CharacterProfileUpdatePromptBuilder(),
            NewIdentityResolverClient(),
            NoopCanonicalNameNormalizationClient(),
            new CharacterCanonicalNameNormalizationPromptBuilder(),
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
            new CharacterProfileUpdatePromptBuilder(),
            NewIdentityResolverClient(),
            NoopCanonicalNameNormalizationClient(),
            new CharacterCanonicalNameNormalizationPromptBuilder(),
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
            CharacterId: 1,
            Name: "John",
            ObservedNameForms: ["Johnny"],
            ObservedNameFormExamples: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
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
                NameForm("Johnny", "Johnny laughed."))));

        var generator = new CharacterDossiersGenerator(
            documentContext,
            dossierService,
            limits,
            NullLogger<CharacterDossiersGenerator>.Instance,
            extractionModelClient,
            new CharacterExtractionPromptBuilder(),
            NoopPatchClient(),
            new CharacterProfileUpdatePromptBuilder(),
            NewIdentityResolverClient(),
            NoopCanonicalNameNormalizationClient(),
            new CharacterCanonicalNameNormalizationPromptBuilder(),
            NewVectorSearchTool());

        var evidence = new[] { new AiTextEditor.Core.Model.EvidenceItem("1.p1", "Johnny laughed.", null) };
        await generator.UpdateFromEvidenceBatchAsync(evidence);

        var updated = dossierService.TryGetDossier(1);
        Assert.NotNull(updated);
        Assert.Equal("Johnny waved.", updated!.ObservedNameFormExamples["Johnny"]);
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
                NameForm("John's", "John's hat was on the table."))));

        var generator = new CharacterDossiersGenerator(
            documentContext,
            dossierService,
            limits,
            NullLogger<CharacterDossiersGenerator>.Instance,
            extractionModelClient,
            new CharacterExtractionPromptBuilder(),
            NoopPatchClient(),
            new CharacterProfileUpdatePromptBuilder(),
            NewIdentityResolverClient(),
            NoopCanonicalNameNormalizationClient(),
            new CharacterCanonicalNameNormalizationPromptBuilder(),
            NewVectorSearchTool());

        var evidence = new[] { new AiTextEditor.Core.Model.EvidenceItem("p1", "John's hat was on the table.", null) };
        var dossiers = await generator.UpdateFromEvidenceBatchAsync(evidence);

        Assert.Single(dossiers.Characters);
        var character = dossiers.Characters[0];
        Assert.Contains("John's", character.ObservedNameFormExamples.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("John", character.ObservedNameFormExamples.Keys, StringComparer.OrdinalIgnoreCase);
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
            new CharacterProfileUpdatePromptBuilder(),
            NewIdentityResolverClient(),
            NoopCanonicalNameNormalizationClient(),
            new CharacterCanonicalNameNormalizationPromptBuilder(),
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
            new CharacterProfileUpdatePromptBuilder(),
            NewIdentityResolverClient(),
            NoopCanonicalNameNormalizationClient(),
            new CharacterCanonicalNameNormalizationPromptBuilder(),
            NewVectorSearchTool());
        var runner = new CharacterBibleWorkflowRunner(generator, NullLoggerFactory.Instance);

        await runner.RunAsync(new CharacterBibleWorkflowInput());

        return extractionModelClient;
    }

    private static CharacterExtractionResponse Response(params ExtractedLocalCharacter[] characters)
        => new(characters);

    private static ExtractedLocalCharacter Character(
        string canonicalName,
        params string[] observedNameForms)
        => new(
            canonicalName,
            "unknown",
            observedNameForms,
            ["p1"]);

    private static string NameForm(string form, string excerpt) => form;

    private static CharacterDossiersGenerator CreateGenerator(
        IDocumentContext documentContext,
        CharacterDossierService dossierService,
        ICharacterExtractionModelClient extractionModelClient,
        ICharacterCanonicalNameNormalizationModelClient canonicalNameNormalizationModelClient,
        ICharacterProfileUpdateModelClient? patchModelClient = null)
    {
        return new CharacterDossiersGenerator(
            documentContext,
            dossierService,
            new CharacterBibleExtractionLimits { MaxParagraphsPerBatch = 256, MaxBatchBytes = 1024 * 128 },
            NullLogger<CharacterDossiersGenerator>.Instance,
            extractionModelClient,
            new CharacterExtractionPromptBuilder(),
            patchModelClient ?? NoopPatchClient(),
            new CharacterProfileUpdatePromptBuilder(),
            NewIdentityResolverClient(),
            canonicalNameNormalizationModelClient,
            new CharacterCanonicalNameNormalizationPromptBuilder(),
            NewVectorSearchTool());
    }

    private static ICharacterProfileUpdateModelClient NoopPatchClient() => new NoopCharacterProfileUpdateModelClient();

    private static ICharacterIdentityResolutionModelClient NewIdentityResolverClient()
        => new SearchBackedIdentityResolutionModelClient();

    private static ICharacterCanonicalNameNormalizationModelClient NoopCanonicalNameNormalizationClient()
        => new NoopCharacterCanonicalNameNormalizationModelClient();

    private static ICharacterVectorSearchTool NewVectorSearchTool()
        => new TestCharacterVectorSearchTool();

    private static async Task<ProfileUpdateTestResult> GenerateWithProfileUpdateAsync(
        AiTextEditor.Core.Model.CharacterProfile currentProfile,
        params ProfileFieldReplacement[] replacements)
    {
        var dossierService = new CharacterDossierService();
        dossierService.UpsertDossier(new AiTextEditor.Core.Model.CharacterDossier(
            CharacterId: 1,
            Name: "John",
            ObservedNameForms: ["John"],
            ObservedNameFormExamples: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["John"] = "John changed."
            },
            Gender: "unknown",
            Profile: currentProfile));

        var repository = new MarkdownDocumentRepository();
        var document = repository.LoadFromMarkdown("John changed.");
        var documentContext = new DocumentContext(document, dossierService);
        var limits = new CharacterBibleExtractionLimits { MaxParagraphsPerBatch = 256, MaxBatchBytes = 1024 * 128 };
        var extractionModelClient = new ScriptedCharacterExtractionModelClient(
            Response(Character("John", NameForm("John", "John changed."))));
        var patchClient = new ScriptedCharacterProfileUpdateModelClient(replacements);
        var generator = new CharacterDossiersGenerator(
            documentContext,
            dossierService,
            limits,
            NullLogger<CharacterDossiersGenerator>.Instance,
            extractionModelClient,
            new CharacterExtractionPromptBuilder(),
            patchClient,
            new CharacterProfileUpdatePromptBuilder(),
            NewIdentityResolverClient(),
            NoopCanonicalNameNormalizationClient(),
            new CharacterCanonicalNameNormalizationPromptBuilder(),
            NewVectorSearchTool());

        var dossier = Assert.Single((await generator.GenerateAsync()).Characters);
        return new ProfileUpdateTestResult(dossier);
    }

    private static ProfileFieldReplacement[] ReadyPatch(
        string? appearance = null,
        string? statusAndCompetence = null,
        string? psychologicalProfile = null,
        string? speechAndCommunication = null,
        IReadOnlyCollection<CharacterBibleProfileField>? changedFields = null)
    {
        var changes = new List<ProfileFieldReplacement>();
        AddProfileChange(changes, CharacterBibleProfileField.Appearance, appearance, changedFields);
        AddProfileChange(changes, CharacterBibleProfileField.StatusAndCompetence, statusAndCompetence, changedFields);
        AddProfileChange(changes, CharacterBibleProfileField.PsychologicalProfile, psychologicalProfile, changedFields);
        AddProfileChange(changes, CharacterBibleProfileField.SpeechAndCommunication, speechAndCommunication, changedFields);

        return [.. changes];
    }

    private static void AddProfileChange(
        List<ProfileFieldReplacement> changes,
        CharacterBibleProfileField field,
        string? text,
        IReadOnlyCollection<CharacterBibleProfileField>? changedFields)
    {
        if (string.IsNullOrWhiteSpace(text) || changedFields is not null && !changedFields.Contains(field))
        {
            return;
        }

        changes.Add(new ProfileFieldReplacement(
            field,
            text));
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

    private sealed class NoopCharacterProfileUpdateModelClient : ICharacterProfileUpdateModelClient
    {
        public Task<CharacterProfileUpdateModelResult> UpdateProfileAsync(
            CharacterProfileUpdateModelRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new CharacterProfileUpdateModelResult(string.Empty));
        }
    }

    private sealed class NoopCharacterCanonicalNameNormalizationModelClient : ICharacterCanonicalNameNormalizationModelClient
    {
        public Task<CharacterCanonicalNameNormalizationResponse> NormalizeAsync(
            CharacterCanonicalNameNormalizationModelRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new CharacterCanonicalNameNormalizationResponse(
                "insufficient_evidence",
                null,
                "No test normalization."));
        }
    }

    private sealed class ScriptedCanonicalNameNormalizationModelClient(params CharacterCanonicalNameNormalizationResponse[] responses)
        : ICharacterCanonicalNameNormalizationModelClient
    {
        private readonly Queue<CharacterCanonicalNameNormalizationResponse> responses = new(responses);

        public int CallCount { get; private set; }

        public List<CharacterCanonicalNameNormalizationModelRequest> Requests { get; } = [];

        public Task<CharacterCanonicalNameNormalizationResponse> NormalizeAsync(
            CharacterCanonicalNameNormalizationModelRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Requests.Add(request);
            var response = responses.Count > 0
                ? responses.Dequeue()
                : new CharacterCanonicalNameNormalizationResponse(
                    "insufficient_evidence",
                    null,
                    "No scripted response.");
            return Task.FromResult(response);
        }
    }

    private sealed class ScriptedCharacterProfileUpdateModelClient(params ProfileFieldReplacement[] replacements)
        : ICharacterProfileUpdateModelClient
    {
        private readonly Queue<ProfileFieldReplacement> replacements = new(replacements);

        public int CallCount { get; private set; }

        public List<CharacterProfileUpdateModelRequest> Requests { get; } = [];

        public Task<CharacterProfileUpdateModelResult> UpdateProfileAsync(
            CharacterProfileUpdateModelRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Requests.Add(request);
            while (replacements.Count > 0)
            {
                var replacement = replacements.Dequeue();
                request.Tool.ReplaceProfileField(
                    replacement.Field,
                    replacement.Value);
            }

            return Task.FromResult(new CharacterProfileUpdateModelResult(string.Empty));
        }

    }

    private sealed class CapturingCharacterBibleRunLogger : ICharacterBibleRunLogger
    {
        public CharacterBibleRunLogContext Context { get; } = new(
            "test",
            "test.log",
            DateTimeOffset.UnixEpoch);

        public List<(string EventName, string Message)> WarningMessages { get; } = [];

        public List<(string EventName, string Message)> DebugMessages { get; } = [];

        public List<(string EventName, string Message)> ErrorMessages { get; } = [];

        public void Info(string eventName, string message)
        {
        }

        public void Debug(string eventName, string message)
        {
            DebugMessages.Add((eventName, message));
        }

        public void DebugBlock(string eventName, string header, string block)
        {
        }

        public void Warning(string eventName, string message)
        {
            WarningMessages.Add((eventName, message));
        }

        public void Error(string eventName, string message, Exception? exception = null)
        {
            ErrorMessages.Add((eventName, message));
        }
    }

    private sealed record ProfileUpdateTestResult(
        AiTextEditor.Core.Model.CharacterDossier Dossier);

    private sealed record ProfileFieldReplacement(
        CharacterBibleProfileField Field,
        string Value);

    private sealed class SearchBackedIdentityResolutionModelClient : ICharacterIdentityResolutionModelClient
    {
        public async Task<CharacterIdentityResolutionResponse> ResolveAsync(
            CharacterIdentityResolutionModelRequest request,
            CancellationToken cancellationToken = default)
        {
            using var json = JsonDocument.Parse(request.UserPrompt);
            var candidate = json.RootElement.GetProperty("candidate");
            var name = candidate.GetProperty("name").GetString() ?? string.Empty;
            var observedNameForms = candidate.GetProperty("observedNameForms")
                .EnumerateArray()
                .Select(form => form.GetString())
                .Where(form => !string.IsNullOrWhiteSpace(form));
            var query = string.Join(' ', new[] { name }.Concat(observedNameForms!));
            var searchResult = await request.SearchTool.SearchCharactersAsync(query, 5, cancellationToken);
            return searchResult.Hits.Count switch
            {
                0 => new CharacterIdentityResolutionResponse(CharacterIdentityDecision.New, Reason: "No test search hit."),
                1 => new CharacterIdentityResolutionResponse(CharacterIdentityDecision.Existing, searchResult.Hits[0].CharacterId, Reason: "Matched by test search."),
                _ => new CharacterIdentityResolutionResponse(
                    CharacterIdentityDecision.Ambiguous,
                    CharacterIds: searchResult.Hits.Select(hit => hit.CharacterId).ToArray(),
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
                    dossier.ObservedNameForms.Any(form => normalizedQuery.Contains(form, StringComparison.OrdinalIgnoreCase)) ||
                    normalizedQuery.Contains(dossier.Name, StringComparison.OrdinalIgnoreCase))
                .Select(dossier => new CharacterVectorSearchHit(
                    new CharacterVectorSearchCard(
                        dossier.CharacterId,
                        dossier.Name,
                        dossier.Gender,
                        dossier.ObservedNameForms,
                        string.Empty),
                    1d))
                .Take(limit)
                .ToArray();

            return Task.FromResult<IReadOnlyList<CharacterVectorSearchHit>>(hits);
        }
    }
}

