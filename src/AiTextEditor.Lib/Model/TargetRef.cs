namespace AiTextEditor.Lib.Model;

public record TargetRef(
    string Id,
    SemanticPointer Pointer,
    LinearItemType Type,
    string Markdown,
    string Text);
