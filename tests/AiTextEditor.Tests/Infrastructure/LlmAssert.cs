using System.Text.Json;
using System.Text.Json.Serialization;
using AiTextEditor.Agent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace AiTextEditor.Tests.Infrastructure;

internal static class LlmAssert
{
    private const string DefaultBaseUrl = "http://localhost:11434";

    private const string SystemPrompt =
        "You are a strict test assertion engine. Evaluate whether TEXT satisfies CHECK. " +
        "Use only TEXT. Populate the structured response with pass and a short reason. " +
        "If uncertain or CHECK asks for info not in TEXT, return pass=false.";

    internal sealed record LlmAssertResult(bool Pass, string Reason, string RawResponse);

    public static async Task<LlmAssertResult> EvaluateAsync(
        HttpClient httpClient,
        string text,
        string criteria,
        ITestOutputHelper? output = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(criteria);

        var modelId = TestLlmConfiguration.ResolveModel();
        var endpoint = ResolveEndpoint();
        var apiKey = Environment.GetEnvironmentVariable("LLM_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            apiKey = "ollama";
        }

        var client = AgenticFrameworkModelClient.CreateOpenAiCompatible(
            httpClient,
            modelId,
            new Uri(endpoint),
            apiKey,
            NullLoggerFactory.Instance);

        var response = await client.RunAsync<LlmAssertResponse>(
            new AgenticModelRequest<LlmAssertResponse>(
                [
                    new ChatMessage(ChatRole.System, SystemPrompt),
                    new ChatMessage(ChatRole.User, $"TEXT:\n{text}\n\nCHECK:\n{criteria}")
                ],
                InvalidContractError: "llm_assert_response_contract_invalid"),
            cancellationToken);

        var raw = JsonSerializer.Serialize(response);
        var result = new LlmAssertResult(response.Pass, response.Reason ?? string.Empty, raw);

        output?.WriteLine($"[LLM-ASSERT] criteria: {criteria}");
        output?.WriteLine($"[LLM-ASSERT] response: {Truncate(raw)}");
        output?.WriteLine($"[LLM-ASSERT] pass={result.Pass}; reason={result.Reason}");

        return result;
    }

    private static string ResolveEndpoint()
    {
        var baseUrl = Environment.GetEnvironmentVariable("LLM_BASE_URL");
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            baseUrl = DefaultBaseUrl;
        }

        var endpoint = baseUrl.TrimEnd('/');
        if (!endpoint.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            endpoint += "/v1";
        }

        return endpoint;
    }

    private static string Truncate(string text, int maxLength = 2000)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
        {
            return text;
        }

        return text[..maxLength] + $"... (+{text.Length - maxLength} chars)";
    }

    private sealed class LlmAssertResponse
    {
        [JsonRequired]
        [JsonPropertyName("pass")]
        public bool Pass { get; init; }

        [JsonRequired]
        [JsonPropertyName("reason")]
        public string? Reason { get; init; }
    }

}
