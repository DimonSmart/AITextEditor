namespace AiTextEditor.Lib.Model;

public class CommandPlan
{
    public TargetSet? TargetSet { get; set; }

    public string? UserCommand { get; set; }

    public bool Success => TargetSet != null;
}
