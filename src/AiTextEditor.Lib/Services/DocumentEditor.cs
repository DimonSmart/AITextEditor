using AiTextEditor.Lib.Interfaces;
using AiTextEditor.Lib.Model;

namespace AiTextEditor.Lib.Services;

public class DocumentEditor : IDocumentEditor
{
    public void ApplyEdits(Document document, IEnumerable<EditOperation> operations)
    {
        // We modify the list of blocks.
        // Since operations might shift indices, we need to be careful.
        // However, operations are usually based on Block IDs.
        // If we process them sequentially, we should look up by ID each time.
        // This is O(N*M) where N is blocks and M is ops. For a prototype, it's fine.

        var blocks = document.Blocks; // Reference to the list

        foreach (var op in operations)
        {
            if (op.Action == EditActionType.Keep) continue;

            int index = -1;
            if (!string.IsNullOrEmpty(op.TargetBlockId))
            {
                index = blocks.FindIndex(b => b.Id == op.TargetBlockId);
            }

            // If target not found for operations that require it, ignore
            if (index == -1 && op.Action != EditActionType.Keep)
            {
                // Warning: Target block not found
                Console.WriteLine($"Warning: Target block {op.TargetBlockId} not found for action {op.Action}");
                continue;
            }

            switch (op.Action)
            {
                case EditActionType.Replace:
                    if (op.NewBlock != null)
                    {
                        // Preserve ID if needed, or use new one. TZ says "choose and document".
                        // Let's use the NewBlock's ID if provided, or keep old one?
                        // Usually replacing means new content. Let's just swap the object.
                        blocks[index] = op.NewBlock;
                    }
                    break;

                case EditActionType.InsertBefore:
                    if (op.NewBlock != null)
                    {
                        blocks.Insert(index, op.NewBlock);
                    }
                    break;

                case EditActionType.InsertAfter:
                    if (op.NewBlock != null)
                    {
                        // Insert after means at index + 1
                        if (index + 1 < blocks.Count)
                        {
                            blocks.Insert(index + 1, op.NewBlock);
                        }
                        else
                        {
                            blocks.Add(op.NewBlock);
                        }
                    }
                    break;

                case EditActionType.Remove:
                    blocks.RemoveAt(index);
                    break;
            }
        }
    }
}
