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
    string InvalidContractError,
    IProgress<AgenticModelDiagnostic>? Diagnostics = null);

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
                logger.LogError(ex, "Agentic Framework raw model call failed for {ResponseType}.", typeof(TResponse).Name);
                throw;
            }

            var rawText = rawResponse.Text ?? string.Empty;
            if (StructuredJsonResponseRecovery.TryRecover<TResponse>(
                    rawText,
                    ResponseSerializerOptions,
                    out var recoveredResponse,
                    out var extractedJson,
                    out var recoveryError))
            {
                var recoveredFromMalformedText = !string.Equals(rawText, extractedJson, StringComparison.Ordinal);
                if (recoveredFromMalformedText)
                {
                    request.Diagnostics?.Report(new AgenticModelDiagnostic(
                        AgenticModelDiagnosticKind.MalformedResponse,
                        typeof(TResponse).Name,
                        attempt,
                        MaxStructuredResponseAttempts,
                        "Model response parse error. Raw response is available for copying.",
                        rawResponse.ModelId ?? chatOptions.ModelId,
                        RecoveryAction: "raw_response",
                        RecoveryResult: "captured",
                        RawResponse: rawText));
                    request.Diagnostics?.Report(new AgenticModelDiagnostic(
                        AgenticModelDiagnosticKind.RecoverySucceeded,
                        typeof(TResponse).Name,
                        attempt,
                        MaxStructuredResponseAttempts,
                        "Model response JSON recovery succeeded via JsonExtractor.",
                        rawResponse.ModelId ?? chatOptions.ModelId,
                        RecoveryAction: "JsonExtractor",
                        RecoveryResult: "recovered",
                        RawResponse: rawText,
                        ExtractedJson: extractedJson));
                    logger.LogWarning(
                        "Recovered malformed JSON response for {ResponseType} using JsonExtractor. Attempt={Attempt}, MaxAttempts={MaxAttempts}, ModelId={ModelId}, FinishReason={FinishReason}, RawLength={RawLength}, ExtractedLength={ExtractedLength}, RawPreview={RawPreview}, RecoveryAction={RecoveryAction}, RecoveryResult={RecoveryResult}.",
                        typeof(TResponse).Name,
                        attempt,
                        MaxStructuredResponseAttempts,
                        rawResponse.ModelId ?? chatOptions.ModelId,
                        rawResponse.FinishReason,
                        rawText.Length,
                        extractedJson?.Length ?? 0,
                        Truncate(rawText, MaxRawResponsePreviewLength),
                        "JsonExtractor",
                        "recovered");
                }

                if (attempt > 1)
                {
                    request.Diagnostics?.Report(new AgenticModelDiagnostic(
                        AgenticModelDiagnosticKind.RetrySucceeded,
                        typeof(TResponse).Name,
                        attempt,
                        MaxStructuredResponseAttempts,
                        $"Model response retry succeeded on attempt {attempt}/{MaxStructuredResponseAttempts}.",
                        rawResponse.ModelId ?? chatOptions.ModelId,
                        RecoveryAction: "retry",
                        RecoveryResult: "succeeded"));
                }

                return recoveredResponse ?? throw new InvalidOperationException(request.InvalidContractError);
            }

            request.Diagnostics?.Report(new AgenticModelDiagnostic(
                AgenticModelDiagnosticKind.MalformedResponse,
                typeof(TResponse).Name,
                attempt,
                MaxStructuredResponseAttempts,
                "Model response parse error. Raw response is available for copying.",
                rawResponse.ModelId ?? chatOptions.ModelId,
                RecoveryAction: "raw_response",
                RecoveryResult: "captured",
                RawResponse: rawText));
            request.Diagnostics?.Report(new AgenticModelDiagnostic(
                AgenticModelDiagnosticKind.RecoveryFailed,
                typeof(TResponse).Name,
                attempt,
                MaxStructuredResponseAttempts,
                $"Model response JSON recovery failed via JsonExtractor: {recoveryError}",
                rawResponse.ModelId ?? chatOptions.ModelId,
                RecoveryAction: "JsonExtractor",
                RecoveryResult: "failed",
                RawResponse: rawText,
                ExtractedJson: extractedJson,
                Error: recoveryError));
            logger.LogError(
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

            if (attempt >= MaxStructuredResponseAttempts)
            {
                break;
            }

            request.Diagnostics?.Report(new AgenticModelDiagnostic(
                AgenticModelDiagnosticKind.Retry,
                typeof(TResponse).Name,
                attempt + 1,
                MaxStructuredResponseAttempts,
                $"Retrying model call after malformed response (attempt {attempt + 1}/{MaxStructuredResponseAttempts}).",
                rawResponse.ModelId ?? chatOptions.ModelId,
                RecoveryAction: "retry",
                RecoveryResult: "started"));
            logger.LogWarning(
                "Model response was malformed for {ResponseType}. Retrying attempt {Attempt}/{MaxAttempts}. RecoveryAction={RecoveryAction}, RecoveryResult={RecoveryResult}, ModelId={ModelId}.",
                typeof(TResponse).Name,
                attempt + 1,
                MaxStructuredResponseAttempts,
                rawResponse.ModelId ?? chatOptions.ModelId,
                "retry",
                "started");
            messages = BuildRetryMessages(request.Messages, typeof(TResponse).Name);
        }

        throw new InvalidOperationException(request.InvalidContractError);
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
