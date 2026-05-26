using AiTextEditor.Agent;
using AiTextEditor.Agent.CharacterBible;
using AiTextEditor.Agent.CharacterBible.Extraction;
using AiTextEditor.Agent.CharacterBible.Patching;
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
        Assert.Equal(2, dossierService.GetDossiers().Characters.Count);
        Assert.Equal(1, extractionModelClient.CallCount);
    }

    [Fact]
    public async Task RunAsync_RefreshesChangedPointersThroughWorkflow()
    {
        var runner = CreateRunner(
            "John arrived.\n\nJohnny smiled.",
            Response(Character(
                "John",
                Alias("Johnny", "Johnny smiled."))),
            out var dossierService,
            out _);

        dossierService.UpsertDossier(new CharacterDossier(
            CharacterId: "c1",
            Name: "John",
            Aliases: ["John"],
            AliasExamples: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
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

        var updated = dossierService.TryGetDossier("c1");
        Assert.NotNull(updated);
        Assert.Contains("Johnny", updated!.AliasExamples.Keys, StringComparer.OrdinalIgnoreCase);
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
            CharacterId: "c1",
            Name: "John",
            Aliases: [],
            AliasExamples: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            Gender: "unknown"));

        await runner.RunAsync();

        var updated = dossierService.TryGetDossier("c1");
        Assert.NotNull(updated);
        Assert.InRange(updated!.ImportanceLevel.GetValueOrDefault(), 1, 10);
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
            CharacterId: "c1",
            Name: "John",
            Aliases: [],
            AliasExamples: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            Gender: "unknown",
            ImportanceLevel: 8));

        await runner.RunAsync();

        var updated = dossierService.TryGetDossier("c1");
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
            CharacterId: "c1",
            Name: "John",
            Aliases: [],
            AliasExamples: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            Gender: "unknown",
            ImportanceLevel: 9));

        await runner.RunAsync(new CharacterBibleWorkflowInput(["p1"]));

        var updated = dossierService.TryGetDossier("c1");
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

        await runner.RunAsync(new CharacterBibleWorkflowInput(["p1"]));

        var dossier = Assert.Single(dossierService.GetDossiers().Characters);
        Assert.NotNull(dossier.ImportanceLevel);
        Assert.InRange(dossier.ImportanceLevel.Value, 1, 4);
    }

    [Fact]
    public async Task RunAsync_AmbiguousCandidate_DoesNotCreateNewDossier()
    {
        var patchClient = new CountingDossierPatchProposalModelClient();
        var runner = CreateRunner(
            "Alex Prime arrived.",
            Response(Character(
                "Alex Prime",
                Alias("Alex Prime", "Alex Prime arrived."))),
            out var dossierService,
            out _,
            patchModelClient: patchClient);

        dossierService.UpsertDossier(new CharacterDossier(
            CharacterId: "c1",
            Name: "Alexander Reed",
            Aliases: ["Alex Prime"],
            AliasExamples: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Alex Prime"] = "Alex Prime opened the door."
            },
            Gender: "unknown"));

        dossierService.UpsertDossier(new CharacterDossier(
            CharacterId: "c2",
            Name: "Alexandra Stone",
            Aliases: ["Alex Prime"],
            AliasExamples: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
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
        Assert.Equal(["c1", "c2"], decision.CandidateIds.OrderBy(id => id, StringComparer.Ordinal).ToArray());

        Assert.Equal(2, dossierService.GetDossiers().Characters.Count);
        Assert.DoesNotContain(dossierService.FindByNameOrAlias("Alex Prime"), dossier => dossier.Name == "Alex Prime");
        Assert.All(dossierService.GetDossiers().Characters, dossier => Assert.Null(dossier.ImportanceLevel));
        Assert.Equal(0, patchClient.CallCount);
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
            new DossierPatchPromptBuilder(),
            ApprovingReviewerClient(),
            new DossierConsistencyReviewerPromptBuilder());
        var runner = new CharacterBibleWorkflowRunner(generator, NullLoggerFactory.Instance);
        var progress = new List<CharacterBibleWorkflowProgress>();

        var result = await runner.RunAsync(
            new CharacterBibleWorkflowInput(),
            new ListProgress(progress),
            CancellationToken.None);

        Assert.Equal("generated", result.Status);
        Assert.Equal(3, result.ParagraphCount);
        Assert.Equal(2, result.CandidateCount);
        Assert.Equal(2, result.Dossiers.Characters.Count);
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
            new DossierPatchPromptBuilder(),
            ApprovingReviewerClient(),
            new DossierConsistencyReviewerPromptBuilder());
        var runner = new CharacterBibleWorkflowRunner(generator, NullLoggerFactory.Instance);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync());

        Assert.Equal("character_extraction_empty_response_content", exception.Message);
    }

    private static CharacterBibleWorkflowRunner CreateRunner(
        string markdown,
        CharacterExtractionResponse response,
        out CharacterDossierService dossierService,
        out ScriptedCharacterExtractionModelClient extractionModelClient,
        IDossierPatchProposalModelClient? patchModelClient = null)
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
            new DossierPatchPromptBuilder(),
            ApprovingReviewerClient(),
            new DossierConsistencyReviewerPromptBuilder());

        return new CharacterBibleWorkflowRunner(generator, NullLoggerFactory.Instance);
    }

    private static CharacterExtractionResponse Response(params CharacterExtractionCharacter[] characters)
        => new() { Characters = characters.ToList() };

    private static CharacterExtractionCharacter Character(
        string canonicalName,
        params CharacterExtractionAlias[] aliases)
        => new(
            canonicalName,
            "unknown",
            aliases.ToList(),
            [new CharacterExtractionEvidence("p1", $"{canonicalName} appeared.")]);

    private static CharacterExtractionAlias Alias(string form, string excerpt)
        => new(form, new CharacterExtractionEvidence("p1", excerpt));

    private static IDossierPatchProposalModelClient NoopPatchClient() => new NoopDossierPatchProposalModelClient();

    private static IDossierConsistencyReviewerModelClient ApprovingReviewerClient() => new ApprovingDossierConsistencyReviewerModelClient();

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

    private sealed class CountingDossierPatchProposalModelClient : IDossierPatchProposalModelClient
    {
        public int CallCount { get; private set; }

        public Task<DossierPatchProposal> ProposePatchAsync(
            DossierPatchProposalModelRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new DossierPatchProposal
            {
                Status = "no_useful_changes",
                AliasesToAdd = [],
                ProfilePatch = null,
                Reason = "No test patch."
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
}


