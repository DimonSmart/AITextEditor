using System.Text.Json;
using AiTextEditor.Domain.Tests;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Xunit.Abstractions;

namespace AiTextEditor.Domain.Tests.Infrastructure;

internal static class LlmAssert
{
    private const string DefaultBaseUrl = "http://localhost:11434";

    private const string SystemPrompt =
        "You are a strict test assertion engine. Evaluate whether TEXT satisfies CHECK. " +
        "Use only TEXT. Return a single JSON object: {\"pass\": true|false, \"reason\": \"short\"}. " +
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

        var builder = Kernel.CreateBuilder();
        builder.AddOpenAIChatCompletion(
            modelId: modelId,
            apiKey: apiKey,
            endpoint: new Uri(endpoint),
            httpClient: httpClient);

        var kernel = builder.Build();
        var chatService = kernel.GetRequiredService<IChatCompletionService>();

        var history = new ChatHistory();
        history.AddSystemMessage(SystemPrompt);
        history.AddUserMessage($"TEXT:\n{text}\n\nCHECK:\n{criteria}");

        var settings = new OpenAIPromptExecutionSettings
        {
            Temperature = 0,
            MaxTokens = 256
        };

        var response = await chatService.GetChatMessageContentsAsync(history, settings, kernel, cancellationToken);
        var raw = response.FirstOrDefault()?.Content ?? string.Empty;
        var result = ParseResult(raw);

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

    private static LlmAssertResult ParseResult(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new LlmAssertResult(false, "Empty response from LLM assert.", raw);
        }

        var json = ExtractJson(raw);
        if (string.IsNullOrWhiteSpace(json))
        {
            return new LlmAssertResult(false, "No JSON object found in LLM assert response.", raw);
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            bool? pass = null;
            string reason = string.Empty;

            foreach (var property in root.EnumerateObject())
            {
                if (string.Equals(property.Name, "pass", StringComparison.OrdinalIgnoreCase))
                {
                    pass = property.Value.ValueKind switch
                    {
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        JsonValueKind.String when bool.TryParse(property.Value.GetString(), out var value) => value,
                        _ => null
                    };
                }

                if (string.Equals(property.Name, "reason", StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.String)
                {
                    reason = property.Value.GetString() ?? string.Empty;
                }
            }

            if (pass is null)
            {
                return new LlmAssertResult(false, "Missing pass field in LLM assert response.", raw);
            }

            return new LlmAssertResult(pass.Value, reason, raw);
        }
        catch (Exception ex)
        {
            return new LlmAssertResult(false, $"Invalid JSON in LLM assert response: {ex.Message}", raw);
        }
    }

    private static string? ExtractJson(string raw)
    {
        var trimmed = raw.Trim();
        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        return trimmed.Substring(start, end - start + 1);
    }

    private static string Truncate(string text, int maxLength = 2000)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
        {
            return text;
        }

        return text[..maxLength] + $"... (+{text.Length - maxLength} chars)";
    }
}
