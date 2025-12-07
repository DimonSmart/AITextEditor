namespace AiTextEditor.Lib.Model;

public class LinearItem
{
    public int Index { get; set; }

    public LinearItemType Type { get; set; }

    public int? Level { get; set; }

    public string Markdown { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;

    public LinearPointer Pointer { get; set; } = new LinearPointer(0, new SemanticPointer(Array.Empty<int>(), null));
}
