using AiTextEditor.Lib.Model;

namespace AiTextEditor.Lib.Model.Intent;

public class PlanningResult
{
    public IntentDto? Intent { get; set; }

    public TargetSet? TargetSet { get; set; }

    public bool Success => Intent != null && TargetSet != null;
}
