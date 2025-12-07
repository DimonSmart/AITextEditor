namespace AiTextEditor.Lib.Model;

public record TargetRef(
    string Id,
    string DocumentId,
    LinearPointer Pointer,
    LinearItemType Type,
    string Markdown,
    string Text);
