namespace AiTextEditor.Lib.Model;

public class Block
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public BlockType Type { get; set; }
    public int Level { get; set; }          // For headings (1-6)
    public string Markdown { get; set; } = string.Empty;
    public string PlainText { get; set; } = string.Empty;
    public string? ParentId { get; set; }   // For hierarchy
}
