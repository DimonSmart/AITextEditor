namespace AiTextEditor.Lib.Model;

public record LinearItem(
    int Id,
    int Index,
    LinearItemType Type,
    int? Level,
    string Markdown,
    string Text,
    SemanticPointer Pointer);
