namespace AiTextEditor.Lib.Model;

public class Document
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public List<Block> Blocks { get; set; } = new();

    public LinearDocument LinearDocument { get; set; } = new();

    /// <summary>
    /// Normalized source markdown for this document (LF endings).
    /// </summary>
    public string SourceText { get; set; } = string.Empty;
}
