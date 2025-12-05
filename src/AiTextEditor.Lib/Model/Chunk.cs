namespace AiTextEditor.Lib.Model;

public class Chunk
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public List<string> BlockIds { get; set; } = new();
    public string Markdown { get; set; } = string.Empty;
    public string HeadingPath { get; set; } = string.Empty;
}
