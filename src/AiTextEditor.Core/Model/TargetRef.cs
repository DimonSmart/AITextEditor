namespace AiTextEditor.Core.Model;

public record TargetRef(
    string Id,
    SemanticPointer Pointer,
    LinearItemType Type,
    string Markdown,
    string Text);
