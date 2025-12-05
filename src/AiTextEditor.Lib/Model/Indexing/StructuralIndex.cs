namespace AiTextEditor.Lib.Model.Indexing;

public class StructuralIndexEntry
{
    public string BlockId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Numbering { get; set; } = string.Empty;
    public int Level { get; set; }
    public int StartOffset { get; set; }
    public int EndOffset { get; set; }
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public string StructuralPath { get; set; } = string.Empty;
    public string HeadingPath { get; set; } = string.Empty;
}

public class AuxiliaryEntity
{
    public string Label { get; set; } = string.Empty; // e.g., "Figure 6"
    public string BlockId { get; set; } = string.Empty;
    public string StructuralPath { get; set; } = string.Empty;
    public int StartOffset { get; set; }
    public int EndOffset { get; set; }
}

public class StructuralIndex
{
    public string DocumentId { get; set; } = string.Empty;
    public List<StructuralIndexEntry> Headings { get; set; } = new();
    public List<AuxiliaryEntity> AuxEntities { get; set; } = new();
}
