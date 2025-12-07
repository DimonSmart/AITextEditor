using AiTextEditor.Lib.Model;

namespace AiTextEditor.Lib.Interfaces;

public interface ILlmEditor
{
    Task<List<EditOperation>> GetEditOperationsAsync(
        string targetSetId,
        List<LinearItem> contextItems,
        string rawUserText,
        string instruction,
        CancellationToken ct = default);
}
