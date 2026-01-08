using System.Text;
using System.Text.Json;
using AiTextEditor.Core.Services;
using AiTextEditor.Agent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Services;
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
        var chatService = new CapturingChatCompletionService();

        var generator = new CharacterDossiersGenerator(
            documentContext,
            dossierService,
            limits,
            NullLogger<CharacterDossiersGenerator>.Instance,
            chatService);

        var dossiers = await generator.GenerateAsync();

        Assert.Equal(names.Length, dossiers.Characters.Count);
        Assert.NotNull(chatService.LastUserMessage);

        using var json = JsonDocument.Parse(chatService.LastUserMessage!);
        var paragraphs = json.RootElement.GetProperty("paragraphs");
        Assert.Equal(names.Length, paragraphs.GetArrayLength());
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
            Facts: [],
            Gender: "unknown"));

        var repository = new MarkdownDocumentRepository();
        var document = repository.LoadFromMarkdown("John arrived.");
        var documentContext = new DocumentContext(document, dossierService);
        var limits = new CursorAgentLimits { MaxElements = 256, MaxBytes = 1024 * 128 };

        var chat = new ScriptedChatCompletionService(
            "[{\"canonicalName\":\"John\",\"gender\":\"unknown\",\"aliases\":[{\"form\":\"Johnny\",\"example\":\"Johnny smiled.\"}]}]"
        );

        var generator = new CharacterDossiersGenerator(
            documentContext,
            dossierService,
            limits,
            NullLogger<CharacterDossiersGenerator>.Instance,
            chat);

        var evidence = new[] { new AiTextEditor.Core.Model.EvidenceItem("1.p1", "Johnny smiled.", null) };
        await generator.UpdateFromEvidenceBatchAsync(evidence);

        var updated = dossierService.TryGetDossier("c1");
        Assert.NotNull(updated);
        Assert.Equal("John is a doctor.", updated!.Description);
        Assert.Contains("Johnny", updated.AliasExamples.Keys, StringComparer.OrdinalIgnoreCase);

        Assert.Equal(1, chat.CallCount);
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
            Facts: [],
            Gender: "unknown"));

        var repository = new MarkdownDocumentRepository();
        var document = repository.LoadFromMarkdown("Johnny laughed.");
        var documentContext = new DocumentContext(document, dossierService);
        var limits = new CursorAgentLimits { MaxElements = 256, MaxBytes = 1024 * 128 };

        var chat = new ScriptedChatCompletionService(
            "[{\"canonicalName\":\"John\",\"gender\":\"unknown\",\"aliases\":[{\"form\":\"Johnny\",\"example\":\"Johnny laughed.\"}]}]"
        );

        var generator = new CharacterDossiersGenerator(
            documentContext,
            dossierService,
            limits,
            NullLogger<CharacterDossiersGenerator>.Instance,
            chat);

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

        var chat = new ScriptedChatCompletionService(
            "[{\"canonicalName\":\"John\",\"gender\":\"unknown\",\"aliases\":[{\"form\":\"John's\",\"example\":\"John's hat was on the table.\"}]}]"
        );

        var generator = new CharacterDossiersGenerator(
            documentContext,
            dossierService,
            limits,
            NullLogger<CharacterDossiersGenerator>.Instance,
            chat);

        var evidence = new[] { new AiTextEditor.Core.Model.EvidenceItem("1.p1", "John's hat was on the table.", null) };
        var dossiers = await generator.UpdateFromEvidenceBatchAsync(evidence);

        Assert.Single(dossiers.Characters);
        var character = dossiers.Characters[0];
        Assert.Contains("John's", character.AliasExamples.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("John", character.AliasExamples.Keys, StringComparer.OrdinalIgnoreCase);
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
                        ["gender"] = "unknown",
                        ["aliases"] = Array.Empty<object>()
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

    private sealed class ScriptedChatCompletionService : IChatCompletionService
    {
        private readonly Queue<string> responses;

        public int CallCount { get; private set; }

        public IReadOnlyDictionary<string, object?> Attributes { get; } = new Dictionary<string, object?>();

        public ScriptedChatCompletionService(params string[] responses)
        {
            this.responses = new Queue<string>(responses);
        }

        public Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
            ChatHistory chatHistory,
            PromptExecutionSettings? executionSettings = null,
            Kernel? kernel = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            var payload = responses.Count > 0 ? responses.Dequeue() : "[]";
            return Task.FromResult<IReadOnlyList<ChatMessageContent>>([new ChatMessageContent(AuthorRole.Assistant, payload)]);
        }

        public IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
            ChatHistory chatHistory,
            PromptExecutionSettings? executionSettings = null,
            Kernel? kernel = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            _ = responses.Count > 0 ? responses.Dequeue() : "[]";
            return AsyncEnumerable.Empty<StreamingChatMessageContent>();
        }
    }
}
