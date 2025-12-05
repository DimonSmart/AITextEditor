using AiTextEditor.Lib.Interfaces;

namespace AiTextEditor.Tests.Fakes;

public class InspectableLlmClient : ILlmClient
{
    private readonly Func<string, string> handler;

    public List<string> Prompts { get; } = new();

    public InspectableLlmClient(Func<string, string> handler)
    {
        this.handler = handler;
    }

    public Task<string> CompleteAsync(string prompt, CancellationToken ct = default)
    {
        Prompts.Add(prompt);
        return Task.FromResult(handler(prompt));
    }
}
