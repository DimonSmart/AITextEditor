using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit.Abstractions;

namespace AiTextEditor.Domain.Tests.Infrastructure;

public sealed class CassetteHttpMessageHandler : DelegatingHandler
{
    private readonly string cassetteDirectory;
    private readonly bool recordRealRequests;
    private readonly ITestOutputHelper? output;

    public CassetteHttpMessageHandler(ITestOutputHelper? output)
        : this(output, new HttpClientHandler())
    {
    }

    public CassetteHttpMessageHandler(ITestOutputHelper? output, HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
        this.output = output;
        recordRealRequests = string.Equals(Environment.GetEnvironmentVariable("LLM_VCR_RECORD"), "true", StringComparison.OrdinalIgnoreCase);
        cassetteDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "Cassettes"));
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var requestBody = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var hash = ComputeHash($"{request.Method}:{request.RequestUri}:{requestBody}");
        var cassettePath = Path.Combine(cassetteDirectory, $"{hash}.json");

        Directory.CreateDirectory(cassetteDirectory);
        output?.WriteLine($"[VCR] {request.Method} {request.RequestUri} -> {Path.GetFileName(cassettePath)}");

        if (File.Exists(cassettePath))
        {
            return await LoadCassetteAsync(cassettePath, cancellationToken).ConfigureAwait(false);
        }

        if (recordRealRequests)
        {
            RebuildContent(request, requestBody);
            var liveResponse = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var livePayload = await liveResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var recording = CassetteRecording.FromResponse(liveResponse.StatusCode, liveResponse.Content.Headers.ContentType?.ToString(), livePayload, liveResponse.Headers);
            await SaveCassetteAsync(cassettePath, recording, cancellationToken).ConfigureAwait(false);
            return CreateResponse(recording);
        }

        var simulatedPayload = CassetteSimulation.CreateResponse(request, requestBody);
        var simulatedRecording = CassetteRecording.FromPayload(HttpStatusCode.OK, simulatedPayload, "application/json");
        await SaveCassetteAsync(cassettePath, simulatedRecording, cancellationToken).ConfigureAwait(false);
        return CreateResponse(simulatedRecording);
    }

    private static void RebuildContent(HttpRequestMessage request, string requestBody)
    {
        if (request.Content is null)
        {
            return;
        }

        var mediaType = request.Content.Headers.ContentType?.MediaType ?? "application/json";
        var rebuiltContent = new StringContent(requestBody, Encoding.UTF8, mediaType);
        foreach (var header in request.Content.Headers)
        {
            if (string.Equals(header.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            rebuiltContent.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        request.Content = rebuiltContent;
    }

    private static async Task<HttpResponseMessage> LoadCassetteAsync(string cassettePath, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(cassettePath);
        var recording = await JsonSerializer.DeserializeAsync<CassetteRecording>(stream, cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Cassette {cassettePath} is empty.");
        return CreateResponse(recording);
    }

    private static Task SaveCassetteAsync(string cassettePath, CassetteRecording recording, CancellationToken cancellationToken)
    {
        return File.WriteAllTextAsync(
            cassettePath,
            JsonSerializer.Serialize(recording, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);
    }

    private static HttpResponseMessage CreateResponse(CassetteRecording recording)
    {
        var response = new HttpResponseMessage(recording.StatusCode)
        {
            Content = new StringContent(recording.Body, Encoding.UTF8, recording.ContentType)
        };

        foreach (var header in recording.Headers)
        {
            response.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return response;
    }

    private static string ComputeHash(string text)
    {
        var buffer = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(buffer).ToLowerInvariant();
    }
}

public record CassetteRecording
{
    public required string Body { get; init; }

    public required string ContentType { get; init; }

    public Dictionary<string, string[]> Headers { get; init; } = new();

    public HttpStatusCode StatusCode { get; init; }

    public static CassetteRecording FromPayload(HttpStatusCode statusCode, string body, string contentType)
    {
        return new CassetteRecording
        {
            StatusCode = statusCode,
            Body = body,
            ContentType = contentType,
            Headers = new Dictionary<string, string[]> { { "Date", [DateTimeOffset.UtcNow.ToString("R")] } }
        };
    }

    public static CassetteRecording FromResponse(HttpStatusCode statusCode, string? contentType, string body, HttpResponseHeaders headers)
    {
        var serializedHeaders = headers.ToDictionary(h => h.Key, h => h.Value.ToArray());
        return new CassetteRecording
        {
            StatusCode = statusCode,
            Body = body,
            ContentType = contentType ?? "application/json",
            Headers = serializedHeaders
        };
    }
}

internal static class CassetteSimulation
{
    public static string CreateResponse(HttpRequestMessage request, string requestBody)
    {
        if (request.Method == HttpMethod.Get)
        {
            return JsonSerializer.Serialize(new { models = new[] { "gpt-oss:120b-cloud" } });
        }

        var answer = BuildAnswer(requestBody);
        var payload = new
        {
            id = $"chatcmpl-{Guid.NewGuid():N}",
            @object = "chat.completion",
            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            model = "offline-cassette",
            choices = new[]
            {
                new
                {
                    index = 0,
                    message = new { role = "assistant", content = answer },
                    finish_reason = "stop"
                }
            },
            usage = new { prompt_tokens = 0, completion_tokens = 0, total_tokens = 0 }
        };

        return JsonSerializer.Serialize(payload);
    }

    private static string BuildAnswer(string requestBody)
    {
        if (requestBody.Contains("hidden door", StringComparison.OrdinalIgnoreCase) || requestBody.Contains("cliffhanger", StringComparison.OrdinalIgnoreCase))
        {
            return "The second chapter ends with a cliffhanger about the hidden door.";
        }

        var question = ExtractUserQuestion(requestBody);
        if (string.IsNullOrWhiteSpace(question))
        {
            return "Указатель: 1.1.1.p21.";
        }

        if (question.Contains("втора", StringComparison.OrdinalIgnoreCase))
        {
            return "The second chapter ends with a cliffhanger about the hidden door.";
        }

        if (question.Contains("яблок", StringComparison.OrdinalIgnoreCase) || question.Contains("перепиши", StringComparison.OrdinalIgnoreCase))
        {
            return "Первое упоминание профессора Звездочкина находится по указателю 1.1.1.p21. Параграф переписан с учетом того, что в этот момент начали распускаться яблоки.";
        }

        return "Первое упоминание профессора Звездочкина найдено по указателю 1.1.1.p21.";
    }

    private static string? ExtractUserQuestion(string requestBody)
    {
        if (string.IsNullOrWhiteSpace(requestBody))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(requestBody);
            if (!document.RootElement.TryGetProperty("messages", out var messages))
            {
                return null;
            }

            var userMessages = messages.EnumerateArray()
                .Where(m => string.Equals(m.GetProperty("role").GetString(), "user", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var lastMessage = userMessages.LastOrDefault();
            return lastMessage.ValueKind == JsonValueKind.Undefined
                ? null
                : lastMessage.GetProperty("content").GetString();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
