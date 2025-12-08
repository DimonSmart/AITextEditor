using System.Net.Http.Headers;
using AiTextEditor.Lib.Services.SemanticKernel;

namespace AiTextEditor.Domain.Tests;

public static class TestLlmConfiguration
{
    private const string DefaultBaseUrl = "http://localhost:11434";

    public static HttpClient CreateLlmClient()
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

        return client;
    }

    public static string ResolveModel() => LamaClient.ResolveModelFromEnvironment();
}
