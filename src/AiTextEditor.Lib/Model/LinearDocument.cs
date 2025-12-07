namespace AiTextEditor.Lib.Model;

public class LinearDocument
{
    public List<LinearItem> Items { get; set; } = new();

    public string SourceText { get; set; } = string.Empty;
}
