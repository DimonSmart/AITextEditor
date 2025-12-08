using System.Net.Http.Json;
using AiTextEditor.Lib.Model;

namespace AiTextEditor.Domain.Tests.Llm;

public class LamaClient
{
    private readonly HttpClient httpClient;
    private readonly string model;

    public LamaClient(HttpClient httpClient, string model = "gpt-oss:120b-cloud")
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.model = model;
    }

    public string Model => model;

    public async Task<LamaChatResponse> SummarizeTargetsAsync(TargetSet targetSet, CancellationToken cancellationToken = default)
    {
        var prompt = string.Join("\n", targetSet.Targets.Select(target => target.Text));
        var request = new LamaChatRequest(model, prompt);
        using var response = await httpClient.PostAsJsonAsync("/api/generate", request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadFromJsonAsync<LamaChatResponse>(cancellationToken: cancellationToken);
        return content ?? throw new InvalidOperationException("LLM response was empty.");
    }

    public async Task<string> GenerateAsync(string prompt, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        var request = new LamaChatRequest(model, prompt);
        using var response = await httpClient.PostAsJsonAsync("/api/generate", request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadFromJsonAsync<LamaChatResponse>(cancellationToken: cancellationToken);
        if (content == null)
        {
            throw new InvalidOperationException("LLM response was empty.");
        }

        return content.Content;
    }
}

public record LamaChatRequest(string Model, string Prompt);

public record LamaChatResponse(string Model, string Content);
