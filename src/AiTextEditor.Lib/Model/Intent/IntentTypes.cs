namespace AiTextEditor.Lib.Model.Intent;

public enum IntentScopeType
{
    Unknown = 0,
    Structural,
    SemanticLocal,
    Global
}

public class ScopeDescriptor
{
    public int? ChapterNumber { get; set; }
    public int? SectionNumber { get; set; }
    public int? FigureNumber { get; set; }
    public string? StructuralPath { get; set; }
    public string? SemanticQuery { get; set; }
    public string? ExtraHints { get; set; }
    public bool IsGlobal { get; set; }
}

public class IntentPayload
{
    /// <summary>
    /// Free-form payload fields as simple string values (LLM output is normalized to strings).
    /// </summary>
    public Dictionary<string, string> Fields { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public class IntentDto
{
    public IntentScopeType ScopeType { get; set; } = IntentScopeType.Unknown;
    public ScopeDescriptor ScopeDescriptor { get; set; } = new();
    public IntentPayload Payload { get; set; } = new();
    public string RawJson { get; set; } = string.Empty;
}
