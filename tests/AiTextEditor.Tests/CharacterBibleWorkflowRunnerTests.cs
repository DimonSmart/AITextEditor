using AiTextEditor.Agent;
using System.Text.Json;
using AiTextEditor.Agent.CharacterBible;
using AiTextEditor.Agent.CharacterBible.Extraction;
using AiTextEditor.Agent.CharacterBible.Normalization;
using AiTextEditor.Agent.CharacterBible.Patching;
using AiTextEditor.Agent.CharacterBible.Resolution;
using AiTextEditor.Agent.CharacterBible.VectorSearch;
using AiTextEditor.Core.Model;
using AiTextEditor.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiTextEditor.Tests;

public sealed class CharacterBibleWorkflowRunnerTests
{
    [Fact]
    public async Task RunAsync_GeneratesDossiersThroughWorkflow()
    {
        var runner = CreateRunner(
            "John smiled.\n\nMary waved.",
            Response(
                Character("John"),
                Character("Mary")),
            out var dossierService,
            out var extractionModelClient);

        var result = await runner.RunAsync();

        Assert.Equal("generated", result.Status);
        Assert.Equal(0, result.ChangedPointerCount);
        Assert.Equal(2, result.ParagraphCount);
        Assert.Equal(2, result.CandidateCount);
        Assert.Equal(2, result.DecisionCount);
        Assert.Equal(2, result.Dossiers.Characters.Count);
        Assert.Empty(dossierService.GetDossiers().Characters);
        Assert.Equal(1, extractionModelClient.CallCount);
    }

    [Fact]
    public async Task RunAsync_RefreshesChangedPointersThroughWorkflow()
    {
        var runner = CreateRunner(
            "John arrived.\n\nJohnny smiled.",
            Response(Character(
                "John",
                NameForm("Johnny", "Johnny smiled."))),
            out var dossierService,
            out _);

        dossierService.UpsertDossier(new CharacterDossier(
            CharacterId: 1,
            Name: "John",
            ObservedNameForms: ["John"],
            ObservedNameFormExamples: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["John"] = "John arrived."
            },
            Gender: "unknown"));

        var result = await runner.RunAsync(new CharacterBibleWorkflowInput(["p2"]));

        Assert.Equal("refreshed", result.Status);
        Assert.Equal(1, result.ChangedPointerCount);
        Assert.Equal(1, result.ParagraphCount);
        Assert.Equal(1, result.CandidateCount);
        Assert.Equal(1, result.DecisionCount);

        var updated = Assert.Single(result.Dossiers.Characters);
        Assert.Equal(1, updated.CharacterId);
        Assert.Contains("Johnny", updated.ObservedNameFormExamples.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("Johnny", dossierService.TryGetDossier(1)!.ObservedNameFormExamples.Keys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_FullGenerationSetsImportanceWhenNull()
    {
        var runner = CreateRunner(
            "John arrived.",
            Response(Character("John")),
            out var dossierService,
            out _);

        dossierService.UpsertDossier(new CharacterDossier(
            CharacterId: 1,
            Name: "John",
            ObservedNameForms: [],
            ObservedNameFormExamples: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            Gender: "unknown"));

        var result = await runner.RunAsync();

        var updated = Assert.Single(result.Dossiers.Characters);
        Assert.Equal(1, updated.CharacterId);
        Assert.InRange(updated.ImportanceLevel.GetValueOrDefault(), 1, 10);
        Assert.Null(dossierService.TryGetDossier(1)!.ImportanceLevel);
    }

    [Fact]
    public async Task RunAsync_FullGenerationDoesNotOverwriteExistingImportance()
    {
        var runner = CreateRunner(
            "John arrived.",
            Response(Character("John")),
            out var dossierService,
            out _);

        dossierService.UpsertDossier(new CharacterDossier(
            CharacterId: 1,
            Name: "John",
            ObservedNameForms: [],
            ObservedNameFormExamples: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            Gender: "unknown",
            ImportanceLevel: 8));

        await runner.RunAsync();

        var updated = dossierService.TryGetDossier(1);
        Assert.NotNull(updated);
        Assert.Equal(8, updated!.ImportanceLevel);
    }

    [Fact]
    public async Task RunAsync_IncrementalUpdateDoesNotChangeExistingImportance()
    {
        var runner = CreateRunner(
            "John arrived.",
            Response(Character("John")),
            out var dossierService,
            out _);

        dossierService.UpsertDossier(new CharacterDossier(
            CharacterId: 1,
            Name: "John",
            ObservedNameForms: [],
            ObservedNameFormExamples: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            Gender: "unknown",
            ImportanceLevel: 9));

        await runner.RunAsync(new CharacterBibleWorkflowInput(["p1"]));

        var updated = dossierService.TryGetDossier(1);
        Assert.NotNull(updated);
        Assert.Equal(9, updated!.ImportanceLevel);
    }

    [Fact]
    public async Task RunAsync_IncrementalUpdateCapsNewCharacterImportance()
    {
        var repeatedNewCharacter = Enumerable
            .Range(0, 12)
            .Select(_ => Character("Newcomer"))
            .ToArray();
        var runner = CreateRunner(
            "Newcomer arrived.",
            Response(repeatedNewCharacter),
            out var dossierService,
            out _);

        var result = await runner.RunAsync(new CharacterBibleWorkflowInput(["p1"]));

        var dossier = Assert.Single(result.Dossiers.Characters);
        Assert.NotNull(dossier.ImportanceLevel);
        Assert.InRange(dossier.ImportanceLevel.Value, 1, 4);
    }

    [Fact]
    public async Task RunAsync_AmbiguousCandidate_DoesNotCreateNewDossier()
    {
        var patchClient = new CountingCharacterProfileUpdateModelClient();
        var runner = CreateRunner(
            "Alex Prime arrived.",
            Response(Character(
                "Alex Prime",
                NameForm("Alex Prime", "Alex Prime arrived."))),
            out var dossierService,
            out _,
            patchModelClient: patchClient);

        dossierService.UpsertDossier(new CharacterDossier(
            CharacterId: 1,
            Name: "Alexander Reed",
            ObservedNameForms: ["Alex Prime"],
            ObservedNameFormExamples: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Alex Prime"] = "Alex Prime opened the door."
            },
            Gender: "unknown"));

        dossierService.UpsertDossier(new CharacterDossier(
            CharacterId: 2,
            Name: "Alexandra Stone",
            ObservedNameForms: ["Alex Prime"],
            ObservedNameFormExamples: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Alex Prime"] = "Alex Prime closed the window."
            },
            Gender: "unknown"));

        var result = await runner.RunAsync();

        Assert.Equal(1, result.CandidateCount);
        Assert.Equal(1, result.DecisionCount);
        Assert.Equal(1, result.AmbiguousDecisionCount);

        var decision = Assert.Single(result.Decisions);
        Assert.Equal(CharacterBibleDecisionKind.Ambiguous, decision.Kind);
        Assert.Null(decision.CharacterId);
        Assert.Equal([1, 2], decision.CandidateIds.OrderBy(id => id).ToArray());

        Assert.Equal(2, dossierService.GetDossiers().Characters.Count);
        Assert.DoesNotContain(dossierService.FindByNameOrObservedNameForm("Alex Prime"), dossier => dossier.Name == "Alex Prime");
        Assert.All(dossierService.GetDossiers().Characters, dossier => Assert.Null(dossier.ImportanceLevel));
        Assert.Equal(0, patchClient.CallCount);
    }

    [Fact]
    public async Task RunAsync_DeferDecisionReportsDeferredAndSuspectCounts()
    {
        var runner = CreateRunner(
            "Professor arrived.",
            Response(Character(
                "Professor",
                NameForm("Professor", "Professor arrived."))),
            out var dossierService,
            out _,
            identityModelClient: new DeferIdentityResolutionModelClient());
        dossierService.UpsertDossier(new CharacterDossier(
            CharacterId: 1,
            Name: "Professor Star",
            ObservedNameForms: ["Professor Star"],
            ObservedNameFormExamples: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Professor Star"] = "Professor Star arrived."
            },
            Gender: "unknown"));

        var result = await runner.RunAsync();

        Assert.Equal(1, result.DecisionCount);
        Assert.Equal(0, result.AmbiguousDecisionCount);
        Assert.Equal(1, result.DeferredDecisionCount);
        Assert.Equal(1, result.SuspectCount);
        Assert.Empty(result.Dossiers.EvidenceIndex!);
        var suspect = Assert.Single(result.Dossiers.SuspectArchive!);
        Assert.Equal("Professor", suspect.CanonicalName);
    }

    [Fact]
    public async Task RunAsync_ReportsDetailedProgress()
    {
        var runner = CreateRunner(
            "John smiled.\n\nMary waved.",
            Response(
                Character("John"),
                Character("Mary")),
            out _,
            out _);
        var progress = new List<CharacterBibleWorkflowProgress>();

        await runner.RunAsync(
            new CharacterBibleWorkflowInput(),
            new ListProgress(progress),
            CancellationToken.None);

        Assert.Contains(progress, item => item.Message.StartsWith("Collecting character bible paragraphs.", StringComparison.Ordinal));
        Assert.Contains(progress, item => item.Message.StartsWith("Read book chunk 1:", StringComparison.Ordinal));
        Assert.Contains(progress, item => item.Message.StartsWith("Starting candidate extraction from 2 paragraphs.", StringComparison.Ordinal));
        Assert.Contains(progress, item => item.Message.StartsWith("Batch 1 produced 2 character candidates", StringComparison.Ordinal));
        Assert.Contains(progress, item => item.Message.StartsWith("Resolved candidate 1/2: John -> New.", StringComparison.Ordinal));
        Assert.Contains(progress, item => item.Message.StartsWith("Character bible generated:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_WhenOneExtractionBatchFails_SkipsBatchAndContinues()
    {
        var repository = new MarkdownDocumentRepository();
        var document = repository.LoadFromMarkdown("John arrived.\n\nBroken batch.\n\nMary waved.");
        var dossierService = new CharacterDossierService();
        var documentContext = new DocumentContext(document, dossierService);
        var limits = new CharacterBibleExtractionLimits { MaxParagraphsPerBatch = 1, MaxBatchBytes = 1024 * 128 };
        var extractionModelClient = new SequencedCharacterExtractionModelClient(
            Response(Character("John")),
            new InvalidOperationException("character_extraction_response_contract_invalid"),
            Response(Character("Mary")));
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
        var progress = new List<CharacterBibleWorkflowProgress>();

        var result = await runner.RunAsync(
            new CharacterBibleWorkflowInput(),
            new ListProgress(progress),
            CancellationToken.None);

        Assert.Equal("generated", result.Status);
        Assert.Equal(3, result.ParagraphCount);
        Assert.Equal(1, result.CandidateCount);
        Assert.Single(result.Dossiers.Characters);
        Assert.NotNull(result.ModelResponseErrors);
        Assert.Equal(1, result.ModelResponseErrors.SkippedBatchCount);
        Assert.Equal(1, result.ModelResponseErrors.SkippedParagraphCount);
        Assert.Equal(3, extractionModelClient.CallCount);
        Assert.Contains(progress, item => item.IsError && item.Message.StartsWith("Batch 2 failed and was skipped", StringComparison.Ordinal));
        Assert.Contains(progress, item => item.Message.StartsWith("Candidate extraction finished with warnings:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_WhenExtractionFailsWithNonrecoverableError_PropagatesOriginalError()
    {
        var repository = new MarkdownDocumentRepository();
        var document = repository.LoadFromMarkdown("John arrived.");
        var dossierService = new CharacterDossierService();
        var documentContext = new DocumentContext(document, dossierService);
        var limits = new CharacterBibleExtractionLimits { MaxParagraphsPerBatch = 256, MaxBatchBytes = 1024 * 128 };
        var generator = new CharacterDossiersGenerator(
            documentContext,
            dossierService,
            limits,
            NullLogger<CharacterDossiersGenerator>.Instance,
            new FailingCharacterExtractionModelClient("character_extraction_empty_response_content"),
            new CharacterExtractionPromptBuilder(),
            NoopPatchClient(),
            new CharacterProfileUpdatePromptBuilder(),
            NewIdentityResolverClient(),
            NoopCanonicalNameNormalizationClient(),
            new CharacterCanonicalNameNormalizationPromptBuilder(),
            NewVectorSearchTool());
        var runner = new CharacterBibleWorkflowRunner(generator, NullLoggerFactory.Instance);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync());

        Assert.Equal("character_extraction_empty_response_content", exception.Message);
    }

    private static CharacterBibleWorkflowRunner CreateRunner(
        string markdown,
        CharacterExtractionResponse response,
        out CharacterDossierService dossierService,
        out ScriptedCharacterExtractionModelClient extractionModelClient,
        ICharacterProfileUpdateModelClient? patchModelClient = null,
        ICharacterIdentityResolutionModelClient? identityModelClient = null)
    {
        var repository = new MarkdownDocumentRepository();
        var document = repository.LoadFromMarkdown(markdown);
        dossierService = new CharacterDossierService();
        var documentContext = new DocumentContext(document, dossierService);
        var limits = new CharacterBibleExtractionLimits { MaxParagraphsPerBatch = 256, MaxBatchBytes = 1024 * 128 };
        extractionModelClient = new ScriptedCharacterExtractionModelClient(response);

        var generator = new CharacterDossiersGenerator(
            documentContext,
            dossierService,
            limits,
            NullLogger<CharacterDossiersGenerator>.Instance,
            extractionModelClient,
            new CharacterExtractionPromptBuilder(),
            patchModelClient ?? NoopPatchClient(),
            new CharacterProfileUpdatePromptBuilder(),
            identityModelClient ?? NewIdentityResolverClient(),
            NoopCanonicalNameNormalizationClient(),
            new CharacterCanonicalNameNormalizationPromptBuilder(),
            NewVectorSearchTool());

        return new CharacterBibleWorkflowRunner(generator, NullLoggerFactory.Instance);
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

    private static string[] ReadPromptPointers(CharacterExtractionModelRequest request)
    {
        using var json = JsonDocument.Parse(request.UserPrompt);
        return json.RootElement
            .GetProperty("paragraphs")
            .EnumerateArray()
            .Select(paragraph => paragraph.GetProperty("pointer").GetString() ?? string.Empty)
            .ToArray();
    }

    private static ICharacterProfileUpdateModelClient NoopPatchClient() => new NoopCharacterProfileUpdateModelClient();

    private static ICharacterIdentityResolutionModelClient NewIdentityResolverClient()
        => new SearchBackedIdentityResolutionModelClient();

    private static ICharacterCanonicalNameNormalizationModelClient NoopCanonicalNameNormalizationClient()
        => new NoopCharacterCanonicalNameNormalizationModelClient();

    private static ICharacterVectorSearchTool NewVectorSearchTool()
        => new TestCharacterVectorSearchTool();

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

    private sealed class SequencedCharacterExtractionModelClient(params object[] results) : ICharacterExtractionModelClient
    {
        private readonly Queue<object> results = new(results);

        public int CallCount { get; private set; }

        public Task<CharacterExtractionResponse> ExtractCharactersAsync(
            CharacterExtractionModelRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            var result = results.Count > 0 ? results.Dequeue() : Response();
            if (result is Exception exception)
            {
                throw exception;
            }

            return Task.FromResult((CharacterExtractionResponse)result);
        }
    }

    private sealed class ListProgress(List<CharacterBibleWorkflowProgress> items)
        : IProgress<CharacterBibleWorkflowProgress>
    {
        public void Report(CharacterBibleWorkflowProgress value)
        {
            items.Add(value);
        }
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

    private sealed class CountingCharacterProfileUpdateModelClient : ICharacterProfileUpdateModelClient
    {
        public int CallCount { get; private set; }

        public Task<CharacterProfileUpdateModelResult> UpdateProfileAsync(
            CharacterProfileUpdateModelRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new CharacterProfileUpdateModelResult(string.Empty));
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

    private sealed class DeferIdentityResolutionModelClient : ICharacterIdentityResolutionModelClient
    {
        public Task<CharacterIdentityResolutionResponse> ResolveAsync(
            CharacterIdentityResolutionModelRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new CharacterIdentityResolutionResponse(
                CharacterIdentityDecision.Defer,
                Reason: "Title-only candidate needs local evidence."));
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

