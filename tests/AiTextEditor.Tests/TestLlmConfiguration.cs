using System;
using System.IO;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using AiTextEditor.Tests.Infrastructure;
using AiTextEditor.Agent;
using AiTextEditor.Core.Infrastructure;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace AiTextEditor.Tests;

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

        var innerHandler = new SocketsHttpHandler();
        if (ignoreSsl)
        {
            innerHandler.SslOptions.RemoteCertificateValidationCallback = (message, cert, chain, errors) => true;
        }

        innerHandler.ConnectCallback = async (context, cancellationToken) =>
        {
            var host = context.DnsEndPoint.Host;
            // Fix for "No such host is known" when host contains port
            if (host.Contains(':') && !host.StartsWith('['))
            {
                var parts = host.Split(':');
                if (parts.Length == 2 && int.TryParse(parts[1], out _))
                {
                    host = parts[0];
                }
            }

            var entry = await Dns.GetHostEntryAsync(host, cancellationToken);
            var ip = entry.AddressList.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork)
                     ?? entry.AddressList.FirstOrDefault();

            if (ip == null)
            {
                throw new SocketException((int)SocketError.HostNotFound);
            }

            var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(ip, context.DnsEndPoint.Port), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        };

        HttpMessageHandler finalHandler = innerHandler;

        var user = Environment.GetEnvironmentVariable("LLM_USERNAME");
        var password = Environment.GetEnvironmentVariable("LLM_PASSWORD");

        if (!string.IsNullOrEmpty(password))
        {
            finalHandler = new BasicAuthHandler(user ?? string.Empty, password, innerHandler);
        }

        var handler = new CassetteHttpMessageHandler(output, finalHandler);

        ILogger logger;
        if (output != null)
        {
            // Use the TestLoggerFactory which includes both XUnit output and File logging
            var factory = TestLoggerFactory.Create(output);
            logger = factory.CreateLogger<LlmRequestLoggingHandler>();
        }
        else
        {
            // Fallback to just file logging if no output helper is provided
            var logBodyEnabled = string.Equals(
                Environment.GetEnvironmentVariable("LLM_LOG_BODY"),
                "true",
                StringComparison.OrdinalIgnoreCase);
            using var factory = LoggerFactory.Create(builder =>
            {
                var logPath = SimpleFileLoggerProvider.CreateTimestampedPath(Path.Combine(AppContext.BaseDirectory, "llm_debug.log"));
                builder.AddProvider(new SimpleFileLoggerProvider(logPath));
                builder.AddFilter("AiTextEditor.Agent.CursorAgentRuntime", LogLevel.Information);
                builder.AddFilter("AiTextEditor.Agent.LlmRequestLoggingHandler", logBodyEnabled ? LogLevel.Trace : LogLevel.Debug);
                builder.AddFilter("Microsoft.SemanticKernel.Connectors.OpenAI.OpenAIChatCompletionService", LogLevel.Debug);
                builder.AddFilter("Microsoft.SemanticKernel.KernelFunction", LogLevel.Debug);
                builder.SetMinimumLevel(LogLevel.Trace);
            });
            logger = factory.CreateLogger<LlmRequestLoggingHandler>();
        }

        var loggingHandler = new LlmRequestLoggingHandler(logger, handler);

        var client = new HttpClient(loggingHandler)
        {
            BaseAddress = new Uri(normalizedBaseUrl),
            Timeout = ResolveTimeout()
        };

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        return (client, apiKey);
    }

    private static TimeSpan ResolveTimeout()
    {
        var raw = Environment.GetEnvironmentVariable("LLM_TIMEOUT_MINUTES");
        if (!string.IsNullOrWhiteSpace(raw) &&
            int.TryParse(raw, out var minutes) &&
            minutes > 0)
        {
            return TimeSpan.FromMinutes(minutes);
        }

        return TimeSpan.FromMinutes(120);
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
