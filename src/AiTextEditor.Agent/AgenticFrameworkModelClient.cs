using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAI;

namespace AiTextEditor.Agent;

public interface IAgenticModelClient
{
    Task<AgenticModelCompletion> RunToolOnlyAsync(
        AgenticToolOnlyModelRequest request,
        CancellationToken cancellationToken = default);

    Task<TResponse> RunAsync<TResponse>(
        AgenticModelRequest request,
        CancellationToken cancellationToken = default)
        where TResponse : class
    {
        ArgumentNullException.ThrowIfNull(request);

        return RunAsync<TResponse>(
            new AgenticModelRequest<TResponse>(
                request.Messages,
                request.InvalidContractError,
                Tools: request.Tools,
                Diagnostics: request.Diagnostics),
            cancellationToken);
    }

    Task<TResponse> RunAsync<TResponse>(
        AgenticModelRequest<TResponse> request,
        CancellationToken cancellationToken = default)
        where TResponse : class;
}

public sealed record AgenticModelRequest(
    IReadOnlyList<ChatMessage> Messages,
    string InvalidContractError,
    IReadOnlyList<AITool>? Tools = null,
    IProgress<AgenticModelDiagnostic>? Diagnostics = null);

public sealed record AgenticModelRequest<TResponse>(
    IReadOnlyList<ChatMessage> Messages,
    string InvalidContractError,
    Func<TResponse, AgenticModelValidationResult>? ValidateResponse = null,
    IReadOnlyList<AITool>? Tools = null,
    IProgress<AgenticModelDiagnostic>? Diagnostics = null)
    where TResponse : class;

public sealed record AgenticToolOnlyModelRequest(
    IReadOnlyList<ChatMessage> Messages,
    string OperationName,
    string ModelCallError,
    IReadOnlyList<AITool>? Tools = null,
    IProgress<AgenticModelDiagnostic>? Diagnostics = null);

public sealed record AgenticModelCompletion(string Text);

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
        ILoggerFactory loggerFactory,
        int retryCount = 5)
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
        var chatClient = openAiClient
            .GetChatClient(trimmedModelId)
            .AsIChatClient()
            .AsBuilder()
            .UseFunctionInvocation(loggerFactory)
            .Build(null);
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
        AgenticModelRequest request,
        CancellationToken cancellationToken = default)
        where TResponse : class
    {
        ArgumentNullException.ThrowIfNull(request);

        return await RunAsync<TResponse>(
            new AgenticModelRequest<TResponse>(
                request.Messages,
                request.InvalidContractError,
                Tools: request.Tools,
                Diagnostics: request.Diagnostics),
            cancellationToken).ConfigureAwait(false);
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
            async (messages, token) => await agent.RunAsync<TResponse>(
                messages,
                null,
                JsonSerializerOptions.Web,
                BuildRunOptions(request.Tools),
                token).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<AgenticModelCompletion> RunToolOnlyAsync(
        AgenticToolOnlyModelRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OperationName);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ModelCallError);

        if (request.Messages.Count == 0)
        {
            throw new ArgumentException("At least one chat message is required.", nameof(request));
        }

        var response = await retryStrategy.RunToolOnlyAsync(
            request,
            async (messages, token) => await agent.RunAsync(
                messages,
                null,
                BuildRunOptions(request.Tools),
                token).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
        return new AgenticModelCompletion(response.Text ?? string.Empty);
    }

    private static ChatClientAgentRunOptions? BuildRunOptions(IReadOnlyList<AITool>? tools)
    {
        if (tools is null || tools.Count == 0)
        {
            return null;
        }

        return new ChatClientAgentRunOptions(new ChatOptions
        {
            Tools = tools.ToList(),
            ToolMode = ChatToolMode.Auto
        });
    }
}
