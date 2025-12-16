using System;
using System.Net.Http.Headers;
using AiTextEditor.Domain.Tests.Infrastructure;
using AiTextEditor.Lib.Services.SemanticKernel;
using Xunit.Abstractions;

namespace AiTextEditor.Domain.Tests;

public static class TestLlmConfiguration
{
    private const string DefaultBaseUrl = "http://localhost:11434";

    public static async Task<HttpClient> CreateVerifiedLlmClientAsync(ITestOutputHelper? output = null, CancellationToken cancellationToken = default)
    {
        var (client, apiKey) = CreateLlmClientInternal(output);
        await AssertReachableAsync(client, apiKey, output, cancellationToken).ConfigureAwait(false);
        return client;
    }

    public static string ResolveModel()
    {
        var model = Environment.GetEnvironmentVariable("LLM_MODEL");
        return string.IsNullOrWhiteSpace(model) ? "gpt-oss:120b-cloud" : model;
    }

    private static (HttpClient Client, string? ApiKey) CreateLlmClientInternal(ITestOutputHelper? output)
    {
        var baseUrl = Environment.GetEnvironmentVariable("LLM_BASE_URL");
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            baseUrl = DefaultBaseUrl;
        }
        var normalizedBaseUrl = NormalizeBaseUrl(baseUrl);
        var apiKey = Environment.GetEnvironmentVariable("LLM_API_KEY");

        var ignoreSsl = Environment.GetEnvironmentVariable("LLM_IGNORE_SSL_ERRORS") == "true";
        var innerHandler = new HttpClientHandler();
        if (ignoreSsl)
        {
            innerHandler.ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true;
        }

        HttpMessageHandler finalHandler = innerHandler;

        var user = Environment.GetEnvironmentVariable("LLM_USERNAME");
        var password = Environment.GetEnvironmentVariable("LLM_PASSWORD");

        if (!string.IsNullOrEmpty(password))
        {
            finalHandler = new BasicAuthHandler(user ?? string.Empty, password, innerHandler);
        }

        var handler = new CassetteHttpMessageHandler(output, finalHandler);
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri(normalizedBaseUrl),
            Timeout = TimeSpan.FromMinutes(60)
        };

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        return (client, apiKey);
    }

    private sealed class BasicAuthHandler : DelegatingHandler
    {
        private readonly string headerValue;

        public BasicAuthHandler(string user, string password, HttpMessageHandler innerHandler) : base(innerHandler)
        {
            var byteArray = System.Text.Encoding.UTF8.GetBytes($"{user}:{password}");
            headerValue = Convert.ToBase64String(byteArray);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", headerValue);
            return base.SendAsync(request, cancellationToken);
        }
    }

    private static async Task AssertReachableAsync(HttpClient client, string? apiKey, ITestOutputHelper? output, CancellationToken cancellationToken)
    {
        var authorizationState = string.IsNullOrWhiteSpace(apiKey)
            ? "missing"
            : $"configured ({apiKey.Length} chars)";
        output?.WriteLine($"LLM_BASE_URL={client.BaseAddress}; auth={authorizationState}; model={ResolveModel()}");

        using var response = await client.GetAsync(ResolveApiPath(client.BaseAddress, "tags"), cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException($"LLM endpoint {client.BaseAddress} is unreachable: {(int)response.StatusCode} {response.ReasonPhrase}. Body: {body}");
        }
    }

    private static string NormalizeBaseUrl(string baseUrl)
    {
        var trimmed = baseUrl.TrimEnd('/');
        return trimmed.EndsWith("/api", StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^4] + "/"
            : trimmed + "/";
    }

    private static string ResolveApiPath(Uri? baseAddress, string endpoint)
    {
        var path = baseAddress?.AbsolutePath ?? "/";
        var normalizedPath = path.EndsWith('/') ? path : path + "/";
        var relativeEndpoint = endpoint.TrimStart('/');

        var baseContainsApi = string.Equals(normalizedPath, "/api/", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalizedPath, "/api", StringComparison.OrdinalIgnoreCase);

        return baseContainsApi ? relativeEndpoint : $"api/{relativeEndpoint}";
    }
}
