using System;
using System.Net.Http;
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
        _logBody = string.Equals(Environment.GetEnvironmentVariable("LLM_LOG_BODY"), "true", StringComparison.OrdinalIgnoreCase);
        _maxBodyChars = ResolveMaxBodyChars();
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // _logger.LogInformation("!!! LLM Request Handler Invoked !!!");
        string? requestBody = null;
        var requestLength = request.Content?.Headers.ContentLength;
        if (_logBody && _logger.IsEnabled(LogLevel.Trace) && request.Content != null)
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
            _logger.LogTrace("LLM Request Body: {Content}", Truncate(requestBody, _maxBodyChars));
        }

        var response = await base.SendAsync(request, cancellationToken);

        string? responseBody = null;
        var responseLength = response.Content?.Headers.ContentLength;
        if (_logBody && _logger.IsEnabled(LogLevel.Trace) && response.Content != null)
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
            _logger.LogTrace("LLM Response Body: {Content}", Truncate(responseBody, _maxBodyChars));
        }

        return response;
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

        return 2000;
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
