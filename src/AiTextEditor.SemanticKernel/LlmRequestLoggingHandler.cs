using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace AiTextEditor.SemanticKernel;

public class LlmRequestLoggingHandler : DelegatingHandler
{
    private readonly ILogger _logger;
    private readonly bool _logBody;
    private readonly int _maxBodyChars;

    public LlmRequestLoggingHandler(ILogger logger, HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
        _logger = logger;
        _logBody = true;
        _maxBodyChars = ResolveMaxBodyChars();
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // _logger.LogInformation("!!! LLM Request Handler Invoked !!!");
        string? requestBody = null;
        var requestLength = request.Content?.Headers.ContentLength;

        if (request.Content != null && ShouldRewriteChatCompletionsTokens(request))
        {
            var raw = await request.Content.ReadAsStringAsync(cancellationToken);
            if (TryRewriteMaxCompletionTokensToMaxTokens(raw, out var rewritten))
            {
                request.Content = CloneJsonContent(request.Content, rewritten);
                requestBody = rewritten;
                requestLength = rewritten.Length;
            }
            else if (_logBody && _logger.IsEnabled(LogLevel.Debug))
            {
                requestBody = raw;
                requestLength ??= raw.Length;
            }
        }
        else if (_logBody && _logger.IsEnabled(LogLevel.Debug) && request.Content != null)
        {
            requestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            requestLength ??= requestBody.Length;
        }

        _logger.LogDebug(
            "LLM Request: {Method} {Uri} contentLength={ContentLength}",
            request.Method,
            request.RequestUri,
            requestLength?.ToString() ?? "unknown");

        if (requestBody != null)
        {
            _logger.LogDebug("LLM Request Body: {Content}", Truncate(requestBody, _maxBodyChars));
        }

        var response = await base.SendAsync(request, cancellationToken);

        string? responseBody = null;
        var responseLength = response.Content?.Headers.ContentLength;
        if (_logBody && _logger.IsEnabled(LogLevel.Debug) && response.Content != null)
        {
            ForceUtf8(response.Content);
            responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            responseLength ??= responseBody.Length;
        }

        _logger.LogDebug(
            "LLM Response: {StatusCode} contentLength={ContentLength}",
            response.StatusCode,
            responseLength?.ToString() ?? "unknown");

        if (responseBody != null)
        {
            _logger.LogDebug("LLM Response Body: {Content}", Truncate(responseBody, _maxBodyChars));
        }

        return response;
    }

    private static bool ShouldRewriteChatCompletionsTokens(HttpRequestMessage request)
    {
        var mode = Environment.GetEnvironmentVariable("LLM_OPENAI_COMPAT_FIX_MAX_TOKENS")?.Trim();
        if (string.Equals(mode, "false", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mode, "0", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mode, "off", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var uri = request.RequestUri;
        if (uri == null)
        {
            return false;
        }

        var path = uri.AbsolutePath ?? string.Empty;
        if (!path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(mode, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mode, "1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mode, "on", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Auto mode: assume localhost OpenAI-compat servers (Ollama/LM Studio/etc).
        return uri.IsLoopback || uri.Port == 11434;
    }

    private static bool TryRewriteMaxCompletionTokensToMaxTokens(string json, out string rewritten)
    {
        rewritten = string.Empty;

        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            var node = JsonNode.Parse(json);
            if (node is not JsonObject obj)
            {
                return false;
            }

            if (!obj.TryGetPropertyValue("max_completion_tokens", out var maxCompletionNode) || maxCompletionNode == null)
            {
                return false;
            }

            if (obj.ContainsKey("max_tokens"))
            {
                return false;
            }

            obj["max_tokens"] = maxCompletionNode.DeepClone();
            obj.Remove("max_completion_tokens");

            rewritten = obj.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = false
            });
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static HttpContent CloneJsonContent(HttpContent original, string body)
    {
        var mediaType = original.Headers.ContentType?.MediaType;
        if (string.IsNullOrWhiteSpace(mediaType))
        {
            mediaType = "application/json";
        }

        var content = new StringContent(body, Encoding.UTF8, mediaType);

        foreach (var header in original.Headers)
        {
            if (string.Equals(header.Key, "Content-Type", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return content;
    }

    private static void ForceUtf8(HttpContent content)
    {
        var contentType = content.Headers.ContentType;
        if (contentType == null)
        {
            return;
        }

        var mediaType = contentType.MediaType;
        if (string.IsNullOrWhiteSpace(mediaType))
        {
            return;
        }

        if (mediaType.StartsWith("application/json", System.StringComparison.OrdinalIgnoreCase) ||
            mediaType.StartsWith("text/", System.StringComparison.OrdinalIgnoreCase))
        {
            contentType.CharSet = "utf-8";
        }
    }

    private static int ResolveMaxBodyChars()
    {
        var raw = Environment.GetEnvironmentVariable("LLM_LOG_BODY_MAX_CHARS");
        if (int.TryParse(raw, out var value) && value > 0)
        {
            return value;
        }

        return 12000;
    }

    private static string Truncate(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
        {
            return text;
        }

        return text[..maxLength] + $"... (+{text.Length - maxLength} chars)";
    }
}
