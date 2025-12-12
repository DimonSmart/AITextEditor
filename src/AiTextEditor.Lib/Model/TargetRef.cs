namespace AiTextEditor.Lib.Model;

public record TargetRef(
    string Id,
    string DocumentId,
    SemanticPointer Pointer,
    LinearItemType Type,
    string Markdown,
    string Text);
