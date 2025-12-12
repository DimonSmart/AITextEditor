namespace AiTextEditor.Lib.Model;

public enum LinearEditAction
{
    Replace,
    InsertBefore,
    InsertAfter,
    Remove,
    Split,
    MergeWithNext,
    MergeWithPrevious
}

public record LinearEditOperation(
    LinearEditAction Action,
    SemanticPointer? TargetPointer,
    int? TargetIndex,
    IReadOnlyList<LinearItem> Items)
{
    public static LinearEditOperation ForIndex(LinearEditAction action, int targetIndex, params LinearItem[] items)
    {
        return new LinearEditOperation(action, null, targetIndex, items);
    }
}
