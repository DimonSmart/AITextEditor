using AiTextEditor.Lib.Model;

namespace AiTextEditor.Lib.Model;

public enum EditActionType
{
    Keep,
    Replace,
    InsertBefore,
    InsertAfter,
    Remove
}

public class EditOperation
{
    public EditActionType Action { get; set; }

    // Target block ID for replace/insert_before/insert_after/remove
    public string? TargetBlockId { get; set; }

    // New block for Replace/InsertBefore/InsertAfter
    public Block? NewBlock { get; set; }
}
