namespace AiTextEditor.Lib.Model;

public class TargetSet
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string DocumentId { get; set; } = string.Empty;

    public string? Label { get; set; }

    public string? IntentJson { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<TargetRef> Targets { get; set; } = new();
}
