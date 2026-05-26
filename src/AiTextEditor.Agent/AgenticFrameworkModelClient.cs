using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAI;

namespace AiTextEditor.Agent;

public interface IAgenticModelClient
{
    Task<TResponse> RunAsync<TResponse>(
        AgenticModelRequest<TResponse> request,
        CancellationToken cancellationToken = default)
        where TResponse : class;
}

public sealed record AgenticModelRequest<TResponse>(
    IReadOnlyList<ChatMessage> Messages,
    string InvalidContractError,
    Func<TResponse, AgenticModelValidationResult>? ValidateResponse = null,
    IProgress<AgenticModelDiagnostic>? Diagnostics = null)
    where TResponse : class;

public sealed record AgenticModelValidationResult(bool IsValid, string Error)
{
    public static AgenticModelValidationResult Valid { get; } = new(true, string.Empty);

    public static AgenticModelValidationResult Invalid(string error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        return new AgenticModelValidationResult(false, error);
    }
}

public sealed class AgenticFrameworkModelClient : IAgenticModelClient
{
    private readonly ChatClientAgent agent;
    private readonly AgenticModelRetryStrategy retryStrategy;

    public AgenticFrameworkModelClient(
        ChatClientAgent agent,
        ILogger<AgenticFrameworkModelClient> logger)
    {
        this.agent = agent ?? throw new ArgumentNullException(nameof(agent));
        ArgumentNullException.ThrowIfNull(logger);
        retryStrategy = new AgenticModelRetryStrategy(logger);
    }

    internal AgenticFrameworkModelClient(
        ChatClientAgent agent,
        AgenticModelRetryStrategy retryStrategy)
    {
        this.agent = agent ?? throw new ArgumentNullException(nameof(agent));
        this.retryStrategy = retryStrategy ?? throw new ArgumentNullException(nameof(retryStrategy));
    }

    public static AgenticFrameworkModelClient CreateOpenAiCompatible(
        HttpClient httpClient,
        string modelId,
        Uri endpoint,
        string apiKey,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        var trimmedModelId = modelId.Trim();
        var clientOptions = new OpenAIClientOptions
        {
            Endpoint = endpoint,
            Transport = new HttpClientPipelineTransport(httpClient),
            NetworkTimeout = TimeSpan.FromMinutes(30)
        };

        var openAiClient = new OpenAIClient(new ApiKeyCredential(apiKey), clientOptions);
        var chatClient = openAiClient.GetChatClient(trimmedModelId).AsIChatClient();
        var agentOptions = new ChatClientAgentOptions
        {
            Name = "ai_text_editor_model",
            ChatOptions = new ChatOptions
            {
                ModelId = trimmedModelId
            },
            UseProvidedChatClientAsIs = true
        };

        var agent = new ChatClientAgent(chatClient, agentOptions, loggerFactory);
        return new AgenticFrameworkModelClient(
            agent,
            new AgenticModelRetryStrategy(loggerFactory.CreateLogger<AgenticModelRetryStrategy>()));
    }

    public async Task<TResponse> RunAsync<TResponse>(
        AgenticModelRequest<TResponse> request,
        CancellationToken cancellationToken = default)
        where TResponse : class
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.InvalidContractError);

        if (request.Messages.Count == 0)
        {
            throw new ArgumentException("At least one chat message is required.", nameof(request));
        }

        return await retryStrategy.RunAsync<TResponse>(
            request,
            (messages, chatOptions, token) => agent.ChatClient.GetResponseAsync(messages, chatOptions, token),
            cancellationToken).ConfigureAwait(false);
    }
}
