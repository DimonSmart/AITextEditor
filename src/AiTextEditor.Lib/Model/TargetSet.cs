namespace AiTextEditor.Lib.Model;

public record TargetSet(
    string Id,
    IReadOnlyList<TargetRef> Targets,
    string? Label,
    string? UserCommand,
    DateTimeOffset CreatedAt)
{
    public static TargetSet Create(
        IReadOnlyList<TargetRef> targets,
        string? userCommand = null,
        string? label = null)
    {
        return new TargetSet(
            Guid.NewGuid().ToString(),
            targets,
            label,
            userCommand,
            DateTimeOffset.UtcNow);
    }
}
