namespace AiTextEditor.Lib.Interfaces;

public interface ILlmClient
{
    Task<string> CompleteAsync(string prompt, CancellationToken ct = default);
}
