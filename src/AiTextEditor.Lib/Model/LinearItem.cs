namespace AiTextEditor.Lib.Model;

public record LinearItem(
    int Index,
    LinearItemType Type,
    int? Level,
    string Markdown,
    string Text,
    LinearPointer Pointer);
