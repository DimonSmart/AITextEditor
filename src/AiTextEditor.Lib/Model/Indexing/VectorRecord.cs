namespace AiTextEditor.Lib.Model.Indexing;

public class VectorRecord
{
    public string DocumentId { get; set; } = string.Empty;
    public string BlockId { get; set; } = string.Empty;
    public string StructuralPath { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public float[] Embedding { get; set; } = Array.Empty<float>();
    public int StartOffset { get; set; }
    public int EndOffset { get; set; }
    public int StartLine { get; set; }
    public int EndLine { get; set; }
}
