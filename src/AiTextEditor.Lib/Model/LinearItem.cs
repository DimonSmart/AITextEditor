namespace AiTextEditor.Lib.Model;

public record LinearItem(
    int Index,
    LinearItemType Type,
    string Markdown,
    string Text,
    SemanticPointer Pointer);
