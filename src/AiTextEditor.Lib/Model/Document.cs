namespace AiTextEditor.Lib.Model;

public class Document
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public List<Block> Blocks { get; set; } = new();
}
