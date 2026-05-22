using AiTextEditor.Agent;
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
                new CharacterExtractionCharacter("John", "unknown", [], "В данном фрагменте характер не раскрыт."),
                new CharacterExtractionCharacter("Mary", "unknown", [], "В данном фрагменте характер не раскрыт.")),
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
            Response(new CharacterExtractionCharacter(
                "John",
                "unknown",
                [new CharacterExtractionAlias("Johnny", "Johnny smiled.")],
                "В данном фрагменте характер не раскрыт.")),
            out var dossierService,
            out _);

        dossierService.UpsertDossier(new CharacterDossier(
            CharacterId: "c1",
            Name: "John",
            Description: "",
            Aliases: ["John"],
            AliasExamples: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["John"] = "John arrived."
            },
            Facts: [],
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
    public async Task RunAsync_AmbiguousCandidate_DoesNotCreateNewDossier()
    {
        var runner = CreateRunner(
            "Alex Prime arrived.",
            Response(new CharacterExtractionCharacter(
                "Alex Prime",
                "unknown",
                [new CharacterExtractionAlias("Alex Prime", "Alex Prime arrived.")],
                "В данном фрагменте характер не раскрыт.")),
            out var dossierService,
            out _);

        dossierService.UpsertDossier(new CharacterDossier(
            CharacterId: "c1",
            Name: "Alexander Reed",
            Description: "",
            Aliases: ["Alex Prime"],
            AliasExamples: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Alex Prime"] = "Alex Prime opened the door."
            },
            Facts: [],
            Gender: "unknown"));

        dossierService.UpsertDossier(new CharacterDossier(
            CharacterId: "c2",
            Name: "Alexandra Stone",
            Description: "",
            Aliases: ["Alex Prime"],
            AliasExamples: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Alex Prime"] = "Alex Prime closed the window."
            },
            Facts: [],
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
    }

    [Fact]
    public async Task RunAsync_WhenExtractionFails_PropagatesOriginalError()
    {
        var repository = new MarkdownDocumentRepository();
        var document = repository.LoadFromMarkdown("John arrived.");
        var dossierService = new CharacterDossierService();
        var documentContext = new DocumentContext(document, dossierService);
        var limits = new CursorAgentLimits { MaxElements = 256, MaxBytes = 1024 * 128 };
        var generator = new CharacterDossiersGenerator(
            documentContext,
            dossierService,
            limits,
            NullLogger<CharacterDossiersGenerator>.Instance,
            new FailingCharacterExtractionModelClient("character_extraction_response_contract_invalid"));
        var runner = new CharacterBibleWorkflowRunner(generator, NullLoggerFactory.Instance);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync());

        Assert.Equal("character_extraction_response_contract_invalid", exception.Message);
    }

    private static CharacterBibleWorkflowRunner CreateRunner(
        string markdown,
        CharacterExtractionResponse response,
        out CharacterDossierService dossierService,
        out ScriptedCharacterExtractionModelClient extractionModelClient)
    {
        var repository = new MarkdownDocumentRepository();
        var document = repository.LoadFromMarkdown(markdown);
        dossierService = new CharacterDossierService();
        var documentContext = new DocumentContext(document, dossierService);
        var limits = new CursorAgentLimits { MaxElements = 256, MaxBytes = 1024 * 128 };
        extractionModelClient = new ScriptedCharacterExtractionModelClient(response);

        var generator = new CharacterDossiersGenerator(
            documentContext,
            dossierService,
            limits,
            NullLogger<CharacterDossiersGenerator>.Instance,
            extractionModelClient);

        return new CharacterBibleWorkflowRunner(generator, NullLoggerFactory.Instance);
    }

    private static CharacterExtractionResponse Response(params CharacterExtractionCharacter[] characters)
        => new() { Characters = characters.ToList() };

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
