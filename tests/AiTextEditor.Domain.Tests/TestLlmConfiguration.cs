using System.Net.Http.Headers;
using AiTextEditor.Lib.Services.SemanticKernel;
using Xunit.Abstractions;

namespace AiTextEditor.Domain.Tests;

public static class TestLlmConfiguration
{
    private const string DefaultBaseUrl = "http://localhost:11434";

    public static async Task<HttpClient> CreateVerifiedLlmClientAsync(ITestOutputHelper? output = null, CancellationToken cancellationToken = default)
    {
        var (client, apiKey) = CreateLlmClientInternal();
        await AssertReachableAsync(client, apiKey, output, cancellationToken).ConfigureAwait(false);
        return client;
    }

    public static string ResolveModel() => LamaClient.ResolveModelFromEnvironment();

    private static (HttpClient Client, string? ApiKey) CreateLlmClientInternal()
    {
        var baseUrl = Environment.GetEnvironmentVariable("LLM_BASE_URL")
            ?? Environment.GetEnvironmentVariable("OLLAMA_HOST")
            ?? DefaultBaseUrl;
        var apiKey = Environment.GetEnvironmentVariable("LLM_API_KEY")
            ?? Environment.GetEnvironmentVariable("OLLAMA_API_KEY");

        var client = new HttpClient
        {
            BaseAddress = new Uri(baseUrl)
        };

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        return (client, apiKey);
    }

    private static async Task AssertReachableAsync(HttpClient client, string? apiKey, ITestOutputHelper? output, CancellationToken cancellationToken)
    {
        var authorizationState = string.IsNullOrWhiteSpace(apiKey)
            ? "missing"
            : $"configured ({apiKey.Length} chars)";
        output?.WriteLine($"LLM_BASE_URL={client.BaseAddress}; auth={authorizationState}; model={ResolveModel()}");

        using var response = await client.GetAsync("/api/version", cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException($"LLM endpoint {client.BaseAddress} is unreachable: {(int)response.StatusCode} {response.ReasonPhrase}. Body: {body}");
        }
    }
}
