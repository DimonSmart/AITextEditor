namespace AiTextEditor.Lib.Model.Indexing;

public class DocumentIndexes
{
    public TextIndex TextIndex { get; set; } = new();
    public StructuralIndex StructuralIndex { get; set; } = new();
}
