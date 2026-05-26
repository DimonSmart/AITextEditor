using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AiTextEditor.Agent;

internal sealed class AgenticModelRetryStrategy
{
    private static readonly JsonSerializerOptions ResponseSerializerOptions = JsonSerializerOptions.Web;
    private const int MaxStructuredResponseAttempts = 3;
    private const int MaxRawResponsePreviewLength = 4000;

    private readonly ILogger logger;

    public AgenticModelRetryStrategy(ILogger logger)
    {
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<TResponse> RunAsync<TResponse>(
        AgenticModelRequest<TResponse> request,
        Func<IReadOnlyList<ChatMessage>, ChatOptions, CancellationToken, Task<ChatResponse>> sendRequestAsync,
        CancellationToken cancellationToken)
        where TResponse : class
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(sendRequestAsync);

        var messages = request.Messages;
        for (var attempt = 1; attempt <= MaxStructuredResponseAttempts; attempt++)
        {
            var chatOptions = CreateChatOptions<TResponse>();
            ChatResponse rawResponse;
            try
            {
                rawResponse = await sendRequestAsync(messages, chatOptions, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (attempt >= MaxStructuredResponseAttempts)
                {
                    logger.LogError(
                        ex,
                        "Agentic Framework raw model call failed for {ResponseType}. Attempt={Attempt}, MaxAttempts={MaxAttempts}.",
                        typeof(TResponse).Name,
                        attempt,
                        MaxStructuredResponseAttempts);
                    throw;
                }

                var nextAttempt = attempt + 1;
                request.Diagnostics?.Report(new AgenticModelDiagnostic(
                    AgenticModelDiagnosticKind.Retry,
                    typeof(TResponse).Name,
                    nextAttempt,
                    MaxStructuredResponseAttempts,
                    $"Retrying model call after request error (attempt {nextAttempt}/{MaxStructuredResponseAttempts}).",
                    chatOptions.ModelId,
                    Error: ex.Message));
                logger.LogWarning(
                    ex,
                    "Agentic Framework raw model call failed for {ResponseType}. Retrying attempt {Attempt}/{MaxAttempts}.",
                    typeof(TResponse).Name,
                    nextAttempt,
                    MaxStructuredResponseAttempts);
                continue;
            }

            var rawText = rawResponse.Text ?? string.Empty;
            if (TryDeserializeResponse<TResponse>(
                    rawText,
                    out var response,
                    out var parseError))
            {
                var validResponse = response ?? throw new InvalidOperationException(request.InvalidContractError);
                var validation = request.ValidateResponse?.Invoke(validResponse) ?? AgenticModelValidationResult.Valid;
                if (!validation.IsValid)
                {
                    request.Diagnostics?.Report(new AgenticModelDiagnostic(
                        AgenticModelDiagnosticKind.InvalidContract,
                        typeof(TResponse).Name,
                        attempt,
                        MaxStructuredResponseAttempts,
                        "Model response contract error. Raw response is available for copying.",
                        rawResponse.ModelId ?? chatOptions.ModelId,
                        RawResponse: rawText,
                        Error: validation.Error));
                    logger.LogError(
                        "Model response contract validation failed for {ResponseType}. Attempt={Attempt}, MaxAttempts={MaxAttempts}, ModelId={ModelId}, FinishReason={FinishReason}, RawLength={RawLength}, RawPreview={RawPreview}, ValidationError={ValidationError}.",
                        typeof(TResponse).Name,
                        attempt,
                        MaxStructuredResponseAttempts,
                        rawResponse.ModelId ?? chatOptions.ModelId,
                        rawResponse.FinishReason,
                        rawText.Length,
                        Truncate(rawText, MaxRawResponsePreviewLength),
                        validation.Error);

                    if (attempt >= MaxStructuredResponseAttempts)
                    {
                        break;
                    }

                    var contractRetryAttempt = attempt + 1;
                    request.Diagnostics?.Report(new AgenticModelDiagnostic(
                        AgenticModelDiagnosticKind.Retry,
                        typeof(TResponse).Name,
                        contractRetryAttempt,
                        MaxStructuredResponseAttempts,
                        $"Retrying model call after contract error (attempt {contractRetryAttempt}/{MaxStructuredResponseAttempts}).",
                        rawResponse.ModelId ?? chatOptions.ModelId,
                        Error: validation.Error));
                    logger.LogWarning(
                        "Model response contract validation failed for {ResponseType}. Retrying attempt {Attempt}/{MaxAttempts}. ModelId={ModelId}. ValidationError={ValidationError}.",
                        typeof(TResponse).Name,
                        contractRetryAttempt,
                        MaxStructuredResponseAttempts,
                        rawResponse.ModelId ?? chatOptions.ModelId,
                        validation.Error);
                    messages = BuildRetryMessages(request.Messages, typeof(TResponse).Name, validation.Error);
                    continue;
                }

                if (attempt > 1)
                {
                    request.Diagnostics?.Report(new AgenticModelDiagnostic(
                        AgenticModelDiagnosticKind.RetrySucceeded,
                        typeof(TResponse).Name,
                        attempt,
                        MaxStructuredResponseAttempts,
                        $"Model response retry succeeded on attempt {attempt}/{MaxStructuredResponseAttempts}.",
                        rawResponse.ModelId ?? chatOptions.ModelId));
                }

                return validResponse;
            }

            request.Diagnostics?.Report(new AgenticModelDiagnostic(
                AgenticModelDiagnosticKind.MalformedResponse,
                typeof(TResponse).Name,
                attempt,
                MaxStructuredResponseAttempts,
                "Model response parse error. Raw response is available for copying.",
                rawResponse.ModelId ?? chatOptions.ModelId,
                RawResponse: rawText,
                Error: parseError));
            logger.LogError(
                "Failed to parse model response for {ResponseType}. Attempt={Attempt}, MaxAttempts={MaxAttempts}, ModelId={ModelId}, FinishReason={FinishReason}, RawLength={RawLength}, RawPreview={RawPreview}, ParseError={ParseError}.",
                typeof(TResponse).Name,
                attempt,
                MaxStructuredResponseAttempts,
                rawResponse.ModelId ?? chatOptions.ModelId,
                rawResponse.FinishReason,
                rawText.Length,
                Truncate(rawText, MaxRawResponsePreviewLength),
                parseError);

            if (attempt >= MaxStructuredResponseAttempts)
            {
                break;
            }

            var retryAttempt = attempt + 1;
            request.Diagnostics?.Report(new AgenticModelDiagnostic(
                AgenticModelDiagnosticKind.Retry,
                typeof(TResponse).Name,
                retryAttempt,
                MaxStructuredResponseAttempts,
                $"Retrying model call after malformed response (attempt {retryAttempt}/{MaxStructuredResponseAttempts}).",
                rawResponse.ModelId ?? chatOptions.ModelId));
            logger.LogWarning(
                "Model response was malformed for {ResponseType}. Retrying attempt {Attempt}/{MaxAttempts}. ModelId={ModelId}.",
                typeof(TResponse).Name,
                retryAttempt,
                MaxStructuredResponseAttempts,
                rawResponse.ModelId ?? chatOptions.ModelId);
            messages = BuildRetryMessages(request.Messages, typeof(TResponse).Name, parseError);
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

    private static bool TryDeserializeResponse<TResponse>(
        string rawText,
        out TResponse? response,
        out string? error)
        where TResponse : class
    {
        response = null;

        if (string.IsNullOrWhiteSpace(rawText))
        {
            error = "Raw response text is empty.";
            return false;
        }

        try
        {
            response = JsonSerializer.Deserialize<TResponse>(rawText, ResponseSerializerOptions);
            if (response is null)
            {
                error = "JSON deserialized to null.";
                return false;
            }

            error = null;
            return true;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private static IReadOnlyList<ChatMessage> BuildRetryMessages(
        IReadOnlyList<ChatMessage> originalMessages,
        string responseTypeName,
        string? previousError)
    {
        var errorText = string.IsNullOrWhiteSpace(previousError)
            ? string.Empty
            : $" Previous error: {previousError.Trim()}";
        return
        [
            .. originalMessages,
            new ChatMessage(
                ChatRole.System,
                $"The previous response was malformed for the {responseTypeName} schema.{errorText} Return exactly one JSON object that matches the requested schema. Do not include markdown, code fences, comments, or prose outside the JSON object.")
        ];
    }
}
