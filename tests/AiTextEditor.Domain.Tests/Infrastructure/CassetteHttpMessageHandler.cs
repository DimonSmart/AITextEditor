using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit.Abstractions;

namespace AiTextEditor.Domain.Tests.Infrastructure;

public sealed class CassetteHttpMessageHandler : DelegatingHandler
{
    private readonly string _cassetteDirectory;
    private readonly ITestOutputHelper? _output;

    public CassetteHttpMessageHandler(ITestOutputHelper? output)
        : this(output, new HttpClientHandler())
    {
    }

    public CassetteHttpMessageHandler(ITestOutputHelper? output, HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
        _output = output;
        _cassetteDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "Cassettes"));
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var requestBody = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var hash = ComputeHash(request, requestBody);
        var cassettePath = Path.Combine(_cassetteDirectory, $"{hash}.json");

        _output?.WriteLine($"[VCR] {request.Method} {request.RequestUri} -> {Path.GetFileName(cassettePath)}");

        if (File.Exists(cassettePath))
        {
            _output?.WriteLine("[VCR] Cache Hit");
            return await LoadCassetteAsync(cassettePath, cancellationToken).ConfigureAwait(false);
        }

        _output?.WriteLine("[VCR] Cache Miss - Recording...");
        
        RebuildContent(request, requestBody);

        HttpResponseMessage response;
        try 
        {
            response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _output?.WriteLine($"[VCR] Error calling real LLM: {ex.Message}");
            throw;
        }

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        
        var recording = new CassetteRecording
        {
            StatusCode = response.StatusCode,
            Body = responseBody,
            ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/json",
            Headers = response.Headers.ToDictionary(h => h.Key, h => h.Value.ToArray())
        };

        if (response.IsSuccessStatusCode)
        {
            Directory.CreateDirectory(_cassetteDirectory);
            await SaveCassetteAsync(cassettePath, recording, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            _output?.WriteLine($"[VCR] Response status code {response.StatusCode} indicates failure. Not recording.");
        }

        return CreateResponse(recording);
    }

    private static void RebuildContent(HttpRequestMessage request, string requestBody)
    {
        if (request.Content is null) return;

        var mediaType = request.Content.Headers.ContentType?.MediaType ?? "application/json";
        var rebuiltContent = new StringContent(requestBody, Encoding.UTF8, mediaType);
        foreach (var header in request.Content.Headers)
        {
            if (!string.Equals(header.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                rebuiltContent.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }
        request.Content = rebuiltContent;
    }

    private static async Task<HttpResponseMessage> LoadCassetteAsync(string cassettePath, CancellationToken cancellationToken)
    {
        using var stream = File.OpenRead(cassettePath);
        var recording = await JsonSerializer.DeserializeAsync<CassetteRecording>(stream, cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Cassette {cassettePath} is empty.");
        return CreateResponse(recording);
    }

    private static Task SaveCassetteAsync(string cassettePath, CassetteRecording recording, CancellationToken cancellationToken)
    {
        var options = new JsonSerializerOptions 
        { 
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        var json = JsonSerializer.Serialize(recording, options);
        return File.WriteAllTextAsync(cassettePath, json, cancellationToken);
    }

    private static HttpResponseMessage CreateResponse(CassetteRecording recording)
    {
        var mediaType = "application/json";
        if (MediaTypeHeaderValue.TryParse(recording.ContentType, out var headerValue) && headerValue.MediaType != null)
        {
            mediaType = headerValue.MediaType;
        }

        var response = new HttpResponseMessage(recording.StatusCode)
        {
            Content = new StringContent(recording.Body, Encoding.UTF8, mediaType)
        };

        foreach (var header in recording.Headers)
        {
            response.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return response;
    }

    private static string ComputeHash(HttpRequestMessage request, string requestBody)
    {
        var key = $"{request.Method}:{request.RequestUri}:{requestBody}";

        if (request.Content?.Headers.ContentType?.MediaType == "application/json")
        {
            try
            {
                var json = JsonNode.Parse(requestBody);
                if (json is JsonObject obj && (obj.ContainsKey("messages") || obj.ContainsKey("model")))
                {
                    var canonical = new
                    {
                        model = obj["model"]?.ToString(),
                        messages = obj["messages"],
                        tools = obj["tools"],
                        tool_choice = obj["tool_choice"],
                        options = obj["options"]
                    };
                    key = JsonSerializer.Serialize(canonical);
                }
            }
            catch
            {
                // Fallback to full body if parsing fails
            }
        }

        var buffer = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(buffer).ToLowerInvariant();
    }
}

public class CassetteRecording
{
    public HttpStatusCode StatusCode { get; set; }
    public required string Body { get; set; }
    public required string ContentType { get; set; }
    public Dictionary<string, string[]> Headers { get; set; } = [];
}
