// Semantic Kernel marks Ollama integration as experimental in 1.27.0.
// We opt-in here to keep the sample concise.
#pragma warning disable SKEXP0070
using AiTextEditor.Lib.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Ollama;

namespace AiTextEditor.Lib.Services;

/// <summary>
/// ILlmClient implementation backed by Semantic Kernel and the Ollama connector.
/// </summary>
public class SemanticKernelLlmClient : ILlmClient
{
    private readonly IChatCompletionService chatCompletion;

    public SemanticKernelLlmClient(IChatCompletionService chatCompletion)
    {
        this.chatCompletion = chatCompletion;
    }

    public async Task<string> CompleteAsync(string prompt, CancellationToken ct = default)
    {
        var history = new ChatHistory();
        history.AddUserMessage(prompt);

        var response = await chatCompletion.GetChatMessageContentsAsync(history, cancellationToken: ct);
        var last = response.LastOrDefault();
        return last?.Content ?? string.Empty;
    }

    public static SemanticKernelLlmClient CreateOllamaClient(
        string modelId,
        Uri? endpoint = null,
        HttpClient? httpClient = null)
    {
        var builder = Kernel.CreateBuilder();

        if (httpClient != null)
        {
            builder.AddOllamaChatCompletion(modelId, httpClient: httpClient);
        }
        else if (endpoint != null)
        {
            builder.AddOllamaChatCompletion(modelId, endpoint: endpoint);
        }
        else
        {
            builder.AddOllamaChatCompletion(modelId);
        }

        var kernel = builder.Build();
        var chat = kernel.Services.GetRequiredService<IChatCompletionService>();

        return new SemanticKernelLlmClient(chat);
    }
}
#pragma warning restore SKEXP0070
