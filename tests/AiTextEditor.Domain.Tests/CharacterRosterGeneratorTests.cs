using System.Text;
using System.Text.Json;
using AiTextEditor.Lib.Services;
using AiTextEditor.SemanticKernel;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Services;
using Xunit;

namespace AiTextEditor.Domain.Tests;

public sealed class CharacterRosterGeneratorTests
{
    [Fact]
    public async Task GenerateDossiers_DoesNotTrimRosterWhenLimitIsNotSet()
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
        var rosterService = new CharacterRosterService();
        var documentContext = new DocumentContext(document, rosterService);
        var limits = new CursorAgentLimits
        {
            MaxElements = 256,
            MaxBytes = 1024 * 128,
            CharacterRosterMaxCharacters = null
        };
        var chatService = new CapturingChatCompletionService();

        var generator = new CharacterRosterGenerator(
            documentContext,
            rosterService,
            limits,
            NullLogger<CharacterRosterGenerator>.Instance,
            chatService);

        var roster = await generator.GenerateAsync();

        Assert.Equal(names.Length, roster.Characters.Count);
        Assert.NotNull(chatService.LastUserMessage);

        using var json = JsonDocument.Parse(chatService.LastUserMessage!);
        var paragraphs = json.RootElement.GetProperty("paragraphs");
        Assert.Equal(names.Length, paragraphs.GetArrayLength());
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

    private sealed class CapturingChatCompletionService : IChatCompletionService
    {
        public string? LastUserMessage { get; private set; }

        public IReadOnlyDictionary<string, object?> Attributes { get; } = new Dictionary<string, object?>();

        public Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
            ChatHistory chatHistory,
            PromptExecutionSettings? executionSettings = null,
            Kernel? kernel = null,
            CancellationToken cancellationToken = default)
        {
            LastUserMessage = chatHistory.LastOrDefault(m => m.Role == AuthorRole.User)?.Content;
            var payload = BuildCharacterPayload(LastUserMessage);
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
            LastUserMessage = chatHistory.LastOrDefault(m => m.Role == AuthorRole.User)?.Content;
            _ = BuildCharacterPayload(LastUserMessage);
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
                    var name = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                   .FirstOrDefault();
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

                return JsonSerializer.Serialize(characters);
            }
            catch
            {
                return "[]";
            }
        }
    }
}
