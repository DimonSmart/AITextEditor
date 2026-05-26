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
    Task<TResponse> RunAsync<TResponse>(
        AgenticModelRequest request,
        CancellationToken cancellationToken = default)
        where TResponse : class;
}

public sealed record AgenticModelRequest(
    IReadOnlyList<ChatMessage> Messages,
    string InvalidContractError);

public sealed class AgenticFrameworkModelClient : IAgenticModelClient
{
    private static readonly JsonSerializerOptions ResponseSerializerOptions = JsonSerializerOptions.Web;
    private const int MaxStructuredResponseAttempts = 3;
    private const int MaxRawResponsePreviewLength = 4000;

    private readonly ChatClientAgent agent;
    private readonly ILogger<AgenticFrameworkModelClient> logger;

    public AgenticFrameworkModelClient(
        ChatClientAgent agent,
        ILogger<AgenticFrameworkModelClient> logger)
    {
        this.agent = agent ?? throw new ArgumentNullException(nameof(agent));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
            loggerFactory.CreateLogger<AgenticFrameworkModelClient>());
    }

    public async Task<TResponse> RunAsync<TResponse>(
        AgenticModelRequest request,
        CancellationToken cancellationToken = default)
        where TResponse : class
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.InvalidContractError);

        if (request.Messages.Count == 0)
        {
            throw new ArgumentException("At least one chat message is required.", nameof(request));
        }

        var messages = request.Messages;
        for (var attempt = 1; attempt <= MaxStructuredResponseAttempts; attempt++)
        {
            var chatOptions = CreateChatOptions<TResponse>();
            var runOptions = new ChatClientAgentRunOptions(chatOptions);
            try
            {
                var response = await agent.RunAsync<TResponse>(
                    messages,
                    session: null,
                    serializerOptions: ResponseSerializerOptions,
                    runOptions,
                    cancellationToken).ConfigureAwait(false);

                return response.Result ?? throw new InvalidOperationException(request.InvalidContractError);
            }
            catch (JsonException ex)
            {
                if (await TryRecoverFromRawResponseAsync<TResponse>(
                        messages,
                        chatOptions,
                        attempt,
                        ex,
                        cancellationToken).ConfigureAwait(false) is { } recoveredResponse)
                {
                    return recoveredResponse;
                }

                if (attempt >= MaxStructuredResponseAttempts)
                {
                    break;
                }

                logger.LogWarning(
                    ex,
                    "Typed model response was malformed for {ResponseType}. Retrying attempt {Attempt}/{MaxAttempts}. RecoveryAction={RecoveryAction}, RecoveryResult={RecoveryResult}, ModelId={ModelId}.",
                    typeof(TResponse).Name,
                    attempt + 1,
                    MaxStructuredResponseAttempts,
                    "retry",
                    "failed",
                    chatOptions.ModelId);
                messages = BuildRetryMessages(request.Messages, typeof(TResponse).Name);
            }
            catch (InvalidOperationException ex) when (!string.Equals(ex.Message, request.InvalidContractError, StringComparison.Ordinal))
            {
                logger.LogError(ex, "Agentic Framework typed model call failed for {ResponseType}.", typeof(TResponse).Name);
                throw;
            }
        }

        throw new InvalidOperationException(request.InvalidContractError);
    }

    private async Task<TResponse?> TryRecoverFromRawResponseAsync<TResponse>(
        IReadOnlyList<ChatMessage> messages,
        ChatOptions chatOptions,
        int attempt,
        JsonException typedException,
        CancellationToken cancellationToken)
        where TResponse : class
    {
        ChatResponse rawResponse;
        try
        {
            rawResponse = await agent.ChatClient.GetResponseAsync(
                messages,
                chatOptions,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(
                ex,
                "Typed model response was malformed for {ResponseType}, but JSON recovery was skipped because raw response is unavailable. Attempt={Attempt}, MaxAttempts={MaxAttempts}, ModelId={ModelId}, RecoveryAction={RecoveryAction}, RecoveryResult={RecoveryResult}.",
                typeof(TResponse).Name,
                attempt,
                MaxStructuredResponseAttempts,
                chatOptions.ModelId,
                "raw_response",
                "unavailable");
            return null;
        }

        var rawText = rawResponse.Text;
        logger.LogWarning(
            typedException,
            "Typed model response was malformed for {ResponseType}. Attempting JSON recovery. Attempt={Attempt}, MaxAttempts={MaxAttempts}, ModelId={ModelId}, FinishReason={FinishReason}, RawLength={RawLength}, RawPreview={RawPreview}, RecoveryAction={RecoveryAction}.",
            typeof(TResponse).Name,
            attempt,
            MaxStructuredResponseAttempts,
            rawResponse.ModelId ?? chatOptions.ModelId,
            rawResponse.FinishReason,
            rawText.Length,
            Truncate(rawText, MaxRawResponsePreviewLength),
            "JsonExtractor");

        if (StructuredJsonResponseRecovery.TryRecover<TResponse>(
                rawText,
                ResponseSerializerOptions,
                out var recoveredResponse,
                out var extractedJson,
                out var recoveryError))
        {
            logger.LogWarning(
                "Recovered malformed JSON response for {ResponseType} using JsonExtractor. Attempt={Attempt}, MaxAttempts={MaxAttempts}, ModelId={ModelId}, FinishReason={FinishReason}, RawLength={RawLength}, ExtractedLength={ExtractedLength}, RecoveryAction={RecoveryAction}, RecoveryResult={RecoveryResult}.",
                typeof(TResponse).Name,
                attempt,
                MaxStructuredResponseAttempts,
                rawResponse.ModelId ?? chatOptions.ModelId,
                rawResponse.FinishReason,
                rawText.Length,
                extractedJson?.Length ?? 0,
                "JsonExtractor",
                "recovered");
            return recoveredResponse;
        }

        logger.LogError(
            typedException,
            "Failed to recover malformed JSON response for {ResponseType}. Attempt={Attempt}, MaxAttempts={MaxAttempts}, ModelId={ModelId}, FinishReason={FinishReason}, RawLength={RawLength}, RawPreview={RawPreview}, RecoveryAction={RecoveryAction}, RecoveryResult={RecoveryResult}, RecoveryError={RecoveryError}.",
            typeof(TResponse).Name,
            attempt,
            MaxStructuredResponseAttempts,
            rawResponse.ModelId ?? chatOptions.ModelId,
            rawResponse.FinishReason,
            rawText.Length,
            Truncate(rawText, MaxRawResponsePreviewLength),
            "JsonExtractor",
            "failed",
            recoveryError);
        return null;
    }

    private static ChatOptions CreateChatOptions<TResponse>()
        where TResponse : class
    {
        return new ChatOptions
        {
            Temperature = 0,
            ResponseFormat = ChatResponseFormat.ForJsonSchema<TResponse>(
                ResponseSerializerOptions,
                schemaName: typeof(TResponse).Name,
                schemaDescription: $"Structured {typeof(TResponse).Name} response.")
        };
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private static IReadOnlyList<ChatMessage> BuildRetryMessages(
        IReadOnlyList<ChatMessage> originalMessages,
        string responseTypeName)
    {
        return
        [
            .. originalMessages,
            new ChatMessage(
                ChatRole.System,
                $"The previous response was malformed for the {responseTypeName} schema. Return exactly one JSON object that matches the requested schema. Do not include markdown, code fences, comments, or prose outside the JSON object.")
        ];
    }
}
