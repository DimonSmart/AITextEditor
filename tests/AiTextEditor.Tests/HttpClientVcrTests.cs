using System.Net;
using System.Text;
using AiTextEditor.Tests.Infrastructure;
using Xunit;

namespace AiTextEditor.Tests;

public class HttpClientVcrTests
{
    [Fact]
    public async Task RecordsAndReplaysByRequestHash()
    {
        var cassetteDir = Path.Combine(Path.GetTempPath(), "ai-text-editor-vcr", Guid.NewGuid().ToString("N"));
        var callCount = 0;

        var innerHandler = new Func<HttpRequestMessage, HttpResponseMessage>(_ =>
        {
            Interlocked.Increment(ref callCount);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"result\":\"live\"}", Encoding.UTF8, "application/json")
            };
        });

        var vcr = new HttpClientVcr(cassetteDir, new LambdaHandler(innerHandler));
        var client = new HttpClient(vcr);

        var requestBody = "{\"prompt\":\"Hello\"}";

        var first = await client.PostAsync("https://llm.local/chat", new StringContent(requestBody, Encoding.UTF8, "application/json"));
        var second = await client.PostAsync("https://llm.local/chat", new StringContent(requestBody, Encoding.UTF8, "application/json"));

        Assert.Equal(1, callCount);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());

        var cassette = Directory.EnumerateFiles(cassetteDir).Single();
        Assert.Contains("chat", cassette);
        Assert.Contains("post_", Path.GetFileName(cassette), StringComparison.OrdinalIgnoreCase);
    }

    private sealed class LambdaHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> handler;

        public LambdaHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            this.handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(handler(request));
        }
    }
}
