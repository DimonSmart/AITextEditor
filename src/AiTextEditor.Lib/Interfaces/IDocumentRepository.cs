using AiTextEditor.Lib.Model;

namespace AiTextEditor.Lib.Interfaces;

public interface IDocumentRepository
{
    Document LoadFromMarkdownFile(string path);
    void SaveToMarkdownFile(Document document, string path);
}
