using AiTextEditor.Agent;
using AiTextEditor.Agent.CharacterBible.Diagnostics;

namespace AiTextEditor.Agent.CharacterBible;

internal sealed class CharacterBibleAgentDiagnosticProgress(
    IProgress<CharacterBibleWorkflowProgress>? progress,
    string stage,
    string operationName,
    string? diagnosticContext = null) : IProgress<AgenticModelDiagnostic>
{
    public void Report(AgenticModelDiagnostic value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var logger = CharacterBibleRunLogScope.Current;
        var eventPrefix = stage switch
        {
            "resolve" => "resolve",
            "split" => "split",
            _ => stage
        };
        var context = string.IsNullOrWhiteSpace(diagnosticContext)
            ? string.Empty
            : diagnosticContext.Trim() + " ";
        var message = $"{context}operation={LogValueFormatter.Quote(operationName)} responseType={value.ResponseType} attempt={value.Attempt} max={value.MaxAttempts} message={LogValueFormatter.Quote(value.Message)} error={LogValueFormatter.Quote(value.Error)} raw={LogValueFormatter.Quote(LogValueFormatter.ShortText(value.RawResponse))}";
        switch (value.Kind)
        {
            case AgenticModelDiagnosticKind.Retry:
                logger?.Warning($"{eventPrefix}.retry", message);
                break;
            case AgenticModelDiagnosticKind.RetrySucceeded:
                logger?.Info($"{eventPrefix}.retry.succeeded", message);
                break;
            case AgenticModelDiagnosticKind.MalformedResponse:
                logger?.Warning($"{eventPrefix}.malformed_response", message);
                break;
            case AgenticModelDiagnosticKind.InvalidContract:
                logger?.Warning($"{eventPrefix}.contract_error", message);
                break;
        }

        progress?.Report(new CharacterBibleWorkflowProgress(
            stage,
            $"{operationName}: {value.Message}",
            value.RawResponse,
            value.RawResponse is null ? null : "Copy raw response",
            AlwaysVisible: true,
            IsError: value.Kind is AgenticModelDiagnosticKind.MalformedResponse or AgenticModelDiagnosticKind.InvalidContract));
    }
}
