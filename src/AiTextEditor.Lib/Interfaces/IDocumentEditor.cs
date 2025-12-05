using AiTextEditor.Lib.Model;

namespace AiTextEditor.Lib.Interfaces;

public interface IDocumentEditor
{
    void ApplyEdits(Document document, IEnumerable<EditOperation> operations);
}
