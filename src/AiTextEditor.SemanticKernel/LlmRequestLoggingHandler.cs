using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace AiTextEditor.SemanticKernel;

public class LlmRequestLoggingHandler : DelegatingHandler
{
    private readonly ILogger _logger;

    public LlmRequestLoggingHandler(ILogger logger, HttpMessageHandler innerHandler) 
        : base(innerHandler)
    {
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // _logger.LogInformation("!!! LLM Request Handler Invoked !!!");
        if (request.Content != null)
        {
            var content = await request.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogTrace("LLM Request: {Method} {Uri}\n{Content}", request.Method, request.RequestUri, content);
        }
        else
        {
            _logger.LogTrace("LLM Request: {Method} {Uri}", request.Method, request.RequestUri);
        }

        var response = await base.SendAsync(request, cancellationToken);

        if (response.Content != null)
        {
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogTrace("LLM Response: {StatusCode}\n{Content}", response.StatusCode, content);
        }
        else
        {
            _logger.LogTrace("LLM Response: {StatusCode}", response.StatusCode);
        }

        return response;
    }
}
