using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Net.Http.Headers;

namespace AiTextEditor.Tests.Infrastructure;

public class HttpClientVcr : DelegatingHandler
{
    private readonly string cassetteDirectory;

    public HttpClientVcr(string cassetteDirectory, HttpMessageHandler? innerHandler = null)
    {
        this.cassetteDirectory = cassetteDirectory;
        Directory.CreateDirectory(cassetteDirectory);
        InnerHandler = innerHandler ?? new HttpClientHandler();
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = await ReadContentAsync(request, cancellationToken);
        var modelName = ExtractModelName(body);
        var cassettePath = Path.Combine(cassetteDirectory, BuildCassetteName(request, body, modelName));

        if (File.Exists(cassettePath))
        {
            var recordedJson = await File.ReadAllTextAsync(cassettePath, cancellationToken);
            var recorded = JsonSerializer.Deserialize<RecordedResponse>(recordedJson) ?? new RecordedResponse();

            if (IsServerError(recorded.StatusCode))
            {
                // Ignore previously recorded server errors; reissue live request.
                return await base.SendAsync(request, cancellationToken);
            }

            return ToHttpResponse(recorded);
        }

        var liveResponse = await base.SendAsync(request, cancellationToken);
        var payload = await RecordedResponse.FromHttpResponseAsync(liveResponse, cancellationToken);

        if (!IsServerError(payload.StatusCode))
        {
            var serialized = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(cassettePath, serialized, cancellationToken);
        }

        // Rewind response content for caller
        if (liveResponse.Content != null)
        {
            liveResponse.Content = new StringContent(payload.Body ?? string.Empty, Encoding.UTF8);
            CopyHeaders(payload.ContentHeaders, liveResponse.Content.Headers);
        }

        return liveResponse;
    }

    private static async Task<string> ReadContentAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.Content == null)
        {
            return string.Empty;
        }

        var body = await request.Content.ReadAsStringAsync(ct);
        var mediaType = request.Content.Headers.ContentType?.MediaType ?? "application/json";

        request.Content = new StringContent(body, Encoding.UTF8, mediaType);
        return body;
    }

    private static HttpResponseMessage ToHttpResponse(RecordedResponse recorded)
    {
        var response = new HttpResponseMessage(recorded.StatusCode == 0 ? HttpStatusCode.OK : recorded.StatusCode)
        {
            ReasonPhrase = recorded.ReasonPhrase,
            Content = new StringContent(recorded.Body ?? string.Empty, Encoding.UTF8)
        };

        CopyHeaders(recorded.Headers, response.Headers);
        CopyHeaders(recorded.ContentHeaders, response.Content!.Headers);

        return response;
    }

    private static void CopyHeaders(IDictionary<string, string[]?>? source, HttpHeaders destination)
    {
        if (source == null) return;

        foreach (var entry in source)
        {
            if (entry.Value == null) continue;
            destination.TryAddWithoutValidation(entry.Key, entry.Value);
        }
    }

    private static string BuildCassetteName(HttpRequestMessage request, string body, string modelName)
    {
        var target = request.RequestUri?.ToString() ?? "unknown";
        var rawKey = $"{request.Method}:{target}:{modelName}:{body}";

        using var sha = SHA256.Create();
        var hash = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(rawKey))).ToLowerInvariant();
        var shortHash = hash[..16];

        var safeName = SanitizeFileName(target);
        var safeModel = string.IsNullOrWhiteSpace(modelName) ? "model" : SanitizeFileName(modelName);
        return $"{request.Method.Method}_{safeModel}_{safeName}_{shortHash}.json";
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);

        foreach (var ch in name)
        {
            builder.Append(invalid.Contains(ch) ? '_' : ch);
        }

        return builder.ToString();
    }

    private static string ExtractModelName(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("model", out var modelProp) &&
                modelProp.ValueKind == JsonValueKind.String)
            {
                return modelProp.GetString() ?? string.Empty;
            }
        }
        catch (JsonException)
        {
            // ignore
        }

        return string.Empty;
    }

    private static bool IsServerError(HttpStatusCode statusCode)
    {
        var code = (int)statusCode;
        return code >= 500;
    }

    private sealed class RecordedResponse
    {
        public HttpStatusCode StatusCode { get; set; }
        public string? ReasonPhrase { get; set; }
        public string? Body { get; set; }
        public Dictionary<string, string[]?>? Headers { get; set; }
        public Dictionary<string, string[]?>? ContentHeaders { get; set; }

        public static async Task<RecordedResponse> FromHttpResponseAsync(HttpResponseMessage response, CancellationToken ct)
        {
            var body = response.Content == null ? string.Empty : await response.Content.ReadAsStringAsync(ct);

            return new RecordedResponse
            {
                StatusCode = response.StatusCode,
                ReasonPhrase = response.ReasonPhrase,
                Body = body,
                Headers = response.Headers.ToDictionary(h => h.Key, h => h.Value?.ToArray()),
                ContentHeaders = response.Content?.Headers.ToDictionary(h => h.Key, h => h.Value?.ToArray())
            };
        }
    }
}
