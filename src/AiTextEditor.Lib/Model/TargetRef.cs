namespace AiTextEditor.Lib.Model;

public class TargetRef
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string? BlockId { get; set; }

    public int LinearIndex { get; set; }

    public LinearPointer Pointer { get; set; } = new LinearPointer(0, new SemanticPointer(Array.Empty<int>(), null));

    public LinearItemType Type { get; set; }

    public string Markdown { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;
}
