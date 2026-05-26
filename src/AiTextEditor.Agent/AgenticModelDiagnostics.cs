namespace AiTextEditor.Agent;

public enum AgenticModelDiagnosticKind
{
    MalformedResponse,
    RecoverySucceeded,
    RecoveryFailed,
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
    string? RecoveryAction = null,
    string? RecoveryResult = null,
    string? RawResponse = null,
    string? ExtractedJson = null,
    string? Error = null);
