namespace AiTextEditor.Core.Model;

public record LinearDocument(
    string Id,
    IReadOnlyList<LinearItem> Items,
    string SourceText)
{
    public static LinearDocument Empty(string? id = null)
    {
        return new LinearDocument(id ?? Guid.NewGuid().ToString(), Array.Empty<LinearItem>(), string.Empty);
    }
}
