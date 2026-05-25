using AiTextEditor.Agent;
using AiTextEditor.Agent.CharacterBible;

namespace AiTextEditor.Web.Services;

public sealed record CharacterBibleOperationRequest(
    string UserCommand,
    IReadOnlyCollection<string>? ChangedPointers);

public sealed record CharacterBibleOperationEvent(
    CharacterBibleOperationEventType Type,
    string Message,
    DateTimeOffset Timestamp,
    CharacterBibleWorkflowOutput? Output = null,
    Exception? Error = null);

public enum CharacterBibleOperationEventType
{
    Started,
    Progress,
    Completed,
    Failed,
    Cancelled
}

public sealed class CharacterBibleOperationRun
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public required string UserCommand { get; init; }

    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? FinishedAt { get; set; }

    public CharacterBibleOperationStatus Status { get; set; }

    public List<CharacterBibleOperationEvent> Events { get; } = [];
}

public enum CharacterBibleOperationStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Cancelled
}

public sealed record CharacterBibleCommandParseResult(
    bool Success,
    CharacterBibleOperationRequest? Request,
    string? Error)
{
    public static CharacterBibleCommandParseResult Parsed(CharacterBibleOperationRequest request)
        => new(true, request, null);

    public static CharacterBibleCommandParseResult Rejected(string error)
        => new(false, null, error);
}

