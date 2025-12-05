using System.Net.Http.Headers;
using AiTextEditor.Lib.Interfaces;
using AiTextEditor.Lib.Services;
using Microsoft.Extensions.Configuration;

namespace AiTextEditor.Tests.Infrastructure;

/// <summary>
/// Centralized helper for wiring an Ollama-backed ILlmClient for tests.
/// Falls back to local Ollama, but prefers cloud if an API key is available
/// (via user-secrets or environment variables).
/// </summary>
public static class LlmTestHelper
{
    public static LlmClientContext CreateClient(string cassetteName, string cassetteSubdirectory = "llm-services")
    {
        var config = new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .AddUserSecrets<SecretsMarker>(optional: true)
            .Build();

        var apiKey = config["OllamaCloud:ApiKey"];
        var endpoint = config["OllamaCloud:Endpoint"];
        var useCloud = !string.IsNullOrWhiteSpace(apiKey);

        var resolvedEndpoint = useCloud
            ? endpoint ?? "https://ollama.com"
            : Environment.GetEnvironmentVariable("OLLAMA_ENDPOINT") ?? "http://localhost:11434";

        var resolvedModel = Environment.GetEnvironmentVariable("OLLAMA_MODEL")
            ?? (useCloud ? "gpt-oss:120b-cloud" : "qwen3:latest");

        var cassetteDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "cassettes", cassetteSubdirectory, cassetteName));
        var vcr = new HttpClientVcr(cassetteDir);
        var httpClient = new HttpClient(vcr)
        {
            BaseAddress = new Uri(resolvedEndpoint),
            Timeout = TimeSpan.FromMinutes(5)
        };

        if (useCloud)
        {
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        var llmClient = SemanticKernelLlmClient.CreateOllamaClient(
            modelId: resolvedModel,
            httpClient: httpClient);

        return new LlmClientContext(httpClient, llmClient, useCloud, resolvedModel, resolvedEndpoint);
    }

    public sealed class LlmClientContext : IDisposable
    {
        public HttpClient HttpClient { get; }
        public ILlmClient LlmClient { get; }
        public bool UsingCloud { get; }
        public string Model { get; }
        public string Endpoint { get; }

        public LlmClientContext(HttpClient httpClient, ILlmClient llmClient, bool usingCloud, string model, string endpoint)
        {
            HttpClient = httpClient;
            LlmClient = llmClient;
            UsingCloud = usingCloud;
            Model = model;
            Endpoint = endpoint;
        }

        public void Dispose()
        {
            HttpClient.Dispose();
        }
    }

    private sealed class SecretsMarker { }
}
