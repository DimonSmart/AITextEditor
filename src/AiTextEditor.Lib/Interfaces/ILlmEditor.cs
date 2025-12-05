using AiTextEditor.Lib.Model;

namespace AiTextEditor.Lib.Interfaces;

public interface ILlmEditor
{
    Task<List<EditOperation>> GetEditOperationsAsync(
        List<Block> contextBlocks,
        string rawUserText,
        string instruction,
        CancellationToken ct = default);
}
