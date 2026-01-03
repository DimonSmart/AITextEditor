using AiTextEditor.Lib.Model;
using AiTextEditor.Lib.Services;
using AiTextEditor.Lib.Services.SemanticKernel;
using AiTextEditor.SemanticKernel;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiTextEditor.Domain.Tests;

public class CharacterRosterCursorOrchestratorTests
{
    [Fact]
    public async Task BuildRosterAsync_UsesCursorAgentEvidence()
    {
        const string markdown = """
        # Title

        Пончик встретил Знайку и сказал, что слышал про Сиропчика.

        Сиропчик пошёл дальше один.
        """;

        var repository = new MarkdownDocumentRepository();
        var document = repository.LoadFromMarkdown(markdown);
        var rosterService = new CharacterRosterService();
        var documentContext = new DocumentContext(document, rosterService);
        var cursorStore = new CursorRegistry();
        var limits = new CursorAgentLimits { DefaultMaxFound = 50 };
        var generator = new CharacterRosterGenerator(
            documentContext,
            rosterService,
            limits,
            NullLogger<CharacterRosterGenerator>.Instance,
            chatService: null);

        var evidence = new List<EvidenceItem>
        {
            new("1.1.p1", "Пончик встретил Знайку и сказал, что слышал про Сиропчика.", "characters"),
            new("1.1.p2", "Сиропчик пошёл дальше один.", "characters")
        };

        var runtime = new FakeCursorAgentRuntime(evidence);
        var orchestrator = new CharacterRosterCursorOrchestrator(
            documentContext,
            cursorStore,
            runtime,
            generator,
            limits,
            NullLogger<CharacterRosterCursorOrchestrator>.Instance);

        var roster = await orchestrator.BuildRosterAsync(includeDossiers: false);

        Assert.Contains(roster.Characters, c => c.Name.Contains("Пончик", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(roster.Characters, c => c.Name.Contains("Знайка", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(roster.Characters, c => c.Name.Contains("Сиропчик", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class FakeCursorAgentRuntime(IReadOnlyList<EvidenceItem> evidence) : ICursorAgentRuntime
    {
        public Task<CursorAgentResult> RunAsync(string cursorName, CursorAgentRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new CursorAgentResult(true, "ok", Evidence: evidence, CursorComplete: true));
        }

        public Task<CursorAgentStepResult> RunStepAsync(CursorAgentRequest request, CursorPortionView portion, CursorAgentState state, int step, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}
