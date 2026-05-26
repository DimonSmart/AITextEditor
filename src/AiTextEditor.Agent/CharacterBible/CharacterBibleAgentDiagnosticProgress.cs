using AiTextEditor.Agent;

namespace AiTextEditor.Agent.CharacterBible;

internal sealed class CharacterBibleAgentDiagnosticProgress(
    IProgress<CharacterBibleWorkflowProgress>? progress,
    string stage,
    string operationName) : IProgress<AgenticModelDiagnostic>
{
    public void Report(AgenticModelDiagnostic value)
    {
        ArgumentNullException.ThrowIfNull(value);

        progress?.Report(new CharacterBibleWorkflowProgress(
            stage,
            $"{operationName}: {value.Message}",
            value.RawResponse,
            value.RawResponse is null ? null : "Copy raw response",
            AlwaysVisible: true,
            IsError: value.Kind is AgenticModelDiagnosticKind.MalformedResponse or AgenticModelDiagnosticKind.InvalidContract));
    }
}
