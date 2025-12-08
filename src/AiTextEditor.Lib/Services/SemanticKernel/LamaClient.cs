using System.Net.Http.Json;
using System.Text.Json.Serialization;
using AiTextEditor.Lib.Model;

namespace AiTextEditor.Lib.Services.SemanticKernel;

public class LamaClient
{
    private readonly HttpClient httpClient;
    private readonly string model;
    private readonly string apiPrefix;

    public const string DefaultModel = "gpt-oss:120b-cloud";

    public LamaClient(HttpClient httpClient, string? model = null)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        apiPrefix = ResolveApiPrefix(httpClient.BaseAddress);
        this.model = string.IsNullOrWhiteSpace(model) ? ResolveModelFromEnvironment() : model;
    }

    public string Model => model;

    public static string ResolveModelFromEnvironment()
    {
        return Environment.GetEnvironmentVariable("LLM_MODEL")
            ?? Environment.GetEnvironmentVariable("OLLAMA_MODEL")
            ?? DefaultModel;
    }

    public async Task<LamaChatResponse> SummarizeTargetsAsync(TargetSet targetSet, CancellationToken cancellationToken = default)
    {
        var prompt = string.Join("\n", targetSet.Targets.Select(target => target.Text));
        var request = new LamaChatRequest(model, prompt);
        using var response = await httpClient.PostAsJsonAsync($"{apiPrefix}generate", request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadFromJsonAsync<LamaChatResponse>(cancellationToken: cancellationToken);
        var normalized = NormalizeResponse(content);
        if (normalized == null || string.IsNullOrWhiteSpace(normalized.Text))
        {
            throw new InvalidOperationException("LLM response was empty.");
        }

        return normalized;
    }

    public async Task<string> GenerateAsync(string prompt, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        var request = new LamaChatRequest(model, prompt);
        using var response = await httpClient.PostAsJsonAsync($"{apiPrefix}generate", request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadFromJsonAsync<LamaChatResponse>(cancellationToken: cancellationToken);
        var normalized = NormalizeResponse(content);
        if (normalized == null || string.IsNullOrWhiteSpace(normalized.Text))
        {
            throw new InvalidOperationException("LLM response was empty.");
        }

        return normalized.Text;
    }

    private LamaChatResponse? NormalizeResponse(LamaChatResponse? response)
    {
        if (response == null)
        {
            return null;
        }

        var modelId = string.IsNullOrWhiteSpace(response.Model) ? model : response.Model;
        return response with { Model = modelId };
    }

    private static string ResolveApiPrefix(Uri? baseAddress)
    {
        var path = baseAddress?.AbsolutePath ?? "/";
        var normalizedPath = path.EndsWith('/') ? path : path + "/";

        return string.Equals(normalizedPath, "/api/", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalizedPath, "/api", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : "api/";
    }
}

public record LamaChatRequest(string Model, string Prompt);

public record LamaChatResponse(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("content")] string? Content,
    [property: JsonPropertyName("response")] string? Response)
{
    [JsonIgnore]
    public string Text => Content ?? Response ?? string.Empty;
}
