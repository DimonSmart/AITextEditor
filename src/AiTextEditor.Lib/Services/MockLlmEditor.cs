using AiTextEditor.Lib.Interfaces;
using AiTextEditor.Lib.Model;

namespace AiTextEditor.Lib.Services;

public class MockLlmEditor : ILlmEditor
{
    public Task<List<EditOperation>> GetEditOperationsAsync(
        List<Block> contextBlocks,
        string rawUserText,
        string instruction,
        CancellationToken ct = default)
    {
        // Mock implementation:
        // 1. Find the last block in context to insert after.
        // 2. Create a new Paragraph block with the user text.
        // 3. Return InsertAfter operation.

        var operations = new List<EditOperation>();

        if (contextBlocks.Count == 0)
        {
            return Task.FromResult(operations);
        }

        var targetBlock = contextBlocks.Last();

        var newBlock = new Block
        {
            Id = Guid.NewGuid().ToString(),
            Type = BlockType.Paragraph,
            Markdown = rawUserText + "\n", // Ensure newline
            PlainText = rawUserText,
            ParentId = targetBlock.ParentId
        };

        operations.Add(new EditOperation
        {
            Action = EditActionType.InsertAfter,
            TargetBlockId = targetBlock.Id,
            NewBlock = newBlock
        });

        // In a real implementation, we would:
        // 1. Construct a prompt containing the contextBlocks (markdown) and the instruction.
        // 2. Call ILlmClient.CompleteAsync(prompt).
        // 3. Parse the JSON response into List<EditOperation>.

        return Task.FromResult(operations);
    }
}
