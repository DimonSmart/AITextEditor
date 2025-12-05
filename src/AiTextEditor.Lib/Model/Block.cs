namespace AiTextEditor.Lib.Model;

public class Block
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public BlockType Type { get; set; }
    public int Level { get; set; }          // For headings (1-6)
    public string Markdown { get; set; } = string.Empty;
    public string PlainText { get; set; } = string.Empty;
    public string? ParentId { get; set; }   // For hierarchy

    // Source location info (offsets are 0-based, inclusive)
    public int StartOffset { get; set; }
    public int EndOffset { get; set; }
    public int StartLine { get; set; }      // 1-based
    public int EndLine { get; set; }        // 1-based
    public int StartColumn { get; set; }    // 1-based
    public int EndColumn { get; set; }      // 1-based

    // Structural addressing (e.g., "1.2" for heading, "1.2.p3" for paragraph 3 under that section)
    public string StructuralPath { get; set; } = string.Empty;
    public string HeadingPath { get; set; } = string.Empty; // Human-friendly heading chain: "Intro > Basics"
    public string? Numbering { get; set; } // Assigned for headings (e.g., "2.1")
}
