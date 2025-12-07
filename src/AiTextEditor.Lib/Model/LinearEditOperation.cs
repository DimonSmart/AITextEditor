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

public class LinearEditOperation
{
    public LinearEditAction Action { get; set; }

    public LinearPointer? TargetPointer { get; set; }

    public int? TargetIndex { get; set; }

    public List<LinearItem> Items { get; set; } = new();
}
