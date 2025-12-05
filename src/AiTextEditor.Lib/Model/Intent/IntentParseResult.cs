namespace AiTextEditor.Lib.Model.Intent;

public class IntentParseResult
{
    public bool Success { get; set; }
    public IntentDto? Intent { get; set; }
    public string? Error { get; set; }
    public string RawResponse { get; set; } = string.Empty;
}
