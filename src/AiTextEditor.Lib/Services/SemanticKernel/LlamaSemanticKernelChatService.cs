using System.Collections.ObjectModel;
using System.Text;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AiTextEditor.Lib.Services.SemanticKernel;

public sealed class LlamaSemanticKernelChatService : IChatCompletionService
{
    private static readonly IReadOnlyDictionary<string, object?> EmptyAttributes = new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>());
    private readonly LamaClient lamaClient;

    public LlamaSemanticKernelChatService(LamaClient lamaClient)
    {
        this.lamaClient = lamaClient ?? throw new ArgumentNullException(nameof(lamaClient));
    }

    public string ModelId => lamaClient.Model;

    public IReadOnlyDictionary<string, object?> Attributes => EmptyAttributes;

    public async Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings? requestSettings = null,
        Kernel? kernel = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chatHistory);

        var prompt = BuildPrompt(chatHistory);
        var content = await lamaClient.GenerateAsync(prompt, cancellationToken);
        var message = new ChatMessageContent(AuthorRole.Assistant, content, metadata: null);
        return new[] { message };
    }

    public IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings? requestSettings = null,
        Kernel? kernel = null,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("Streaming chat generation is not supported by the LlamaSemanticKernelChatService.");
    }

    public ChatHistory CreateNewChat(string? instructions = null)
    {
        return new ChatHistory(instructions ?? string.Empty);
    }

    private static string BuildPrompt(ChatHistory chatHistory)
    {
        var builder = new StringBuilder();
        foreach (var message in chatHistory)
        {
            var content = message.Content ?? string.Empty;
            builder.AppendLine($"{message.Role.ToString().ToUpperInvariant()}: {content}");
        }

        return builder.ToString();
    }
}
