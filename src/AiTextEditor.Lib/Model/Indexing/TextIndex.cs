using AiTextEditor.Lib.Model;

namespace AiTextEditor.Lib.Model.Indexing;

public class TextIndexEntry
{
    public string BlockId { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public int StartOffset { get; set; }
    public int EndOffset { get; set; }
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public string StructuralPath { get; set; } = string.Empty;
    public BlockType BlockType { get; set; }
}

public class TextIndex
{
    public string DocumentId { get; set; } = string.Empty;
    public List<TextIndexEntry> Entries { get; set; } = new();
}
