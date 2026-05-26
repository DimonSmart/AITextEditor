namespace AiTextEditor.Agent;

public enum AgenticModelDiagnosticKind
{
    MalformedResponse,
    Retry,
    RetrySucceeded
}

public sealed record AgenticModelDiagnostic(
    AgenticModelDiagnosticKind Kind,
    string ResponseType,
    int Attempt,
    int MaxAttempts,
    string Message,
    string? ModelId = null,
    string? RawResponse = null,
    string? Error = null);
