using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AiTextEditor.Agent;

internal sealed class AgenticModelRetryStrategy
{
    private const int MaxStructuredResponseAttempts = 3;
    private const int MaxRawResponsePreviewLength = 4000;

    private readonly ILogger logger;

    public AgenticModelRetryStrategy(ILogger logger)
    {
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<TResponse> RunAsync<TResponse>(
        AgenticModelRequest<TResponse> request,
        Func<IReadOnlyList<ChatMessage>, CancellationToken, Task<AgentResponse<TResponse>>> runAgentAsync,
        CancellationToken cancellationToken)
        where TResponse : class
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(runAgentAsync);

        var messages = request.Messages;
        for (var attempt = 1; attempt <= MaxStructuredResponseAttempts; attempt++)
        {
            AgentResponse<TResponse> agentResponse;
            try
            {
                agentResponse = await runAgentAsync(messages, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (attempt >= MaxStructuredResponseAttempts)
                {
                    logger.LogError(
                        ex,
                        "Agent Framework typed model call failed for {ResponseType}. Attempt={Attempt}, MaxAttempts={MaxAttempts}.",
                        typeof(TResponse).Name,
                        attempt,
                        MaxStructuredResponseAttempts);
                    throw new InvalidOperationException(request.InvalidContractError, ex);
                }

                var nextAttempt = attempt + 1;
                request.Diagnostics?.Report(new AgenticModelDiagnostic(
                    AgenticModelDiagnosticKind.MalformedResponse,
                    typeof(TResponse).Name,
                    attempt,
                    MaxStructuredResponseAttempts,
                    "Agent Framework model call failed before a typed response was produced.",
                    null,
                    Error: ex.Message));
                request.Diagnostics?.Report(new AgenticModelDiagnostic(
                    AgenticModelDiagnosticKind.Retry,
                    typeof(TResponse).Name,
                    nextAttempt,
                    MaxStructuredResponseAttempts,
                    $"Retrying model call after Agent Framework error (attempt {nextAttempt}/{MaxStructuredResponseAttempts}).",
                    null,
                    Error: ex.Message));
                logger.LogWarning(
                    ex,
                    "Agent Framework typed model call failed for {ResponseType}. Retrying attempt {Attempt}/{MaxAttempts}.",
                    typeof(TResponse).Name,
                    nextAttempt,
                    MaxStructuredResponseAttempts);
                messages = BuildRetryMessages(request.Messages, typeof(TResponse).Name, ex.Message);
                continue;
            }

            TResponse response;
            try
            {
                response = agentResponse.Result
                    ?? throw new InvalidOperationException("Agent Framework returned a null typed response.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var responseText = agentResponse.Text ?? string.Empty;
                request.Diagnostics?.Report(new AgenticModelDiagnostic(
                    AgenticModelDiagnosticKind.MalformedResponse,
                    typeof(TResponse).Name,
                    attempt,
                    MaxStructuredResponseAttempts,
                    "Agent Framework could not produce the typed response. Raw response is available for copying.",
                    null,
                    RawResponse: responseText,
                    Error: ex.Message));
                logger.LogError(
                    ex,
                    "Agent Framework typed response conversion failed for {ResponseType}. Attempt={Attempt}, MaxAttempts={MaxAttempts}, RawLength={RawLength}, RawPreview={RawPreview}.",
                    typeof(TResponse).Name,
                    attempt,
                    MaxStructuredResponseAttempts,
                    responseText.Length,
                    Truncate(responseText, MaxRawResponsePreviewLength));

                if (attempt >= MaxStructuredResponseAttempts)
                {
                    break;
                }

                var nextAttempt = attempt + 1;
                request.Diagnostics?.Report(new AgenticModelDiagnostic(
                    AgenticModelDiagnosticKind.Retry,
                    typeof(TResponse).Name,
                    nextAttempt,
                    MaxStructuredResponseAttempts,
                    $"Retrying model call after typed response error (attempt {nextAttempt}/{MaxStructuredResponseAttempts}).",
                    null,
                    Error: ex.Message));
                logger.LogWarning(
                    "Agent Framework typed response conversion failed for {ResponseType}. Retrying attempt {Attempt}/{MaxAttempts}.",
                    typeof(TResponse).Name,
                    nextAttempt,
                    MaxStructuredResponseAttempts);
                messages = BuildRetryMessages(request.Messages, typeof(TResponse).Name, ex.Message);
                continue;
            }

            var validation = request.ValidateResponse?.Invoke(response) ?? AgenticModelValidationResult.Valid;
            if (!validation.IsValid)
            {
                var responseText = agentResponse.Text ?? string.Empty;
                request.Diagnostics?.Report(new AgenticModelDiagnostic(
                    AgenticModelDiagnosticKind.InvalidContract,
                    typeof(TResponse).Name,
                    attempt,
                    MaxStructuredResponseAttempts,
                    "Model response contract error. Raw response is available for copying.",
                    null,
                    RawResponse: responseText,
                    Error: validation.Error));
                logger.LogError(
                    "Model response contract validation failed for {ResponseType}. Attempt={Attempt}, MaxAttempts={MaxAttempts}, RawLength={RawLength}, RawPreview={RawPreview}, ValidationError={ValidationError}.",
                    typeof(TResponse).Name,
                    attempt,
                    MaxStructuredResponseAttempts,
                    responseText.Length,
                    Truncate(responseText, MaxRawResponsePreviewLength),
                    validation.Error);

                if (attempt >= MaxStructuredResponseAttempts)
                {
                    break;
                }

                var nextAttempt = attempt + 1;
                request.Diagnostics?.Report(new AgenticModelDiagnostic(
                    AgenticModelDiagnosticKind.Retry,
                    typeof(TResponse).Name,
                    nextAttempt,
                    MaxStructuredResponseAttempts,
                    $"Retrying model call after contract error (attempt {nextAttempt}/{MaxStructuredResponseAttempts}).",
                    null,
                    Error: validation.Error));
                logger.LogWarning(
                    "Model response contract validation failed for {ResponseType}. Retrying attempt {Attempt}/{MaxAttempts}. ValidationError={ValidationError}.",
                    typeof(TResponse).Name,
                    nextAttempt,
                    MaxStructuredResponseAttempts,
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
                    null));
            }

            return response;
        }

        throw new InvalidOperationException(request.InvalidContractError);
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
                $"The previous response was invalid for the {responseTypeName} schema.{errorText} Return exactly one structured response that matches the requested schema.")
        ];
    }
}
