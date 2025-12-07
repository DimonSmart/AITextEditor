namespace AiTextEditor.Lib.Model;

public record TargetSet(
    string Id,
    string DocumentId,
    IReadOnlyList<TargetRef> Targets,
    string? Label,
    string? UserCommand,
    DateTimeOffset CreatedAt)
{
    public static TargetSet Create(
        string documentId,
        IReadOnlyList<TargetRef> targets,
        string? userCommand = null,
        string? label = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        return new TargetSet(
            Guid.NewGuid().ToString(),
            documentId,
            targets,
            label,
            userCommand,
            DateTimeOffset.UtcNow);
    }
}
