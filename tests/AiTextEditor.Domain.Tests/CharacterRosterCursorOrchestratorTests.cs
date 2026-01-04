using AiTextEditor.Lib.Model;
using AiTextEditor.Lib.Services;
using AiTextEditor.Lib.Services.SemanticKernel;
using AiTextEditor.SemanticKernel;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using System.Text.Json;
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
        var chatService = new TokenizingChatCompletionService();
        var generator = new CharacterRosterGenerator(
            documentContext,
            rosterService,
            limits,
            NullLogger<CharacterRosterGenerator>.Instance,
            chatService);

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
                            ["aliases"] = Array.Empty<string>(),
                            ["gender"] = "unknown",
                            ["description"] = name
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
