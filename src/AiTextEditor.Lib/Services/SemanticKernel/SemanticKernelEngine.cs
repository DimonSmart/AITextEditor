using AiTextEditor.Lib.Model;
using AiTextEditor.Lib.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using System.Linq;
using System.Net.Http.Headers;

namespace AiTextEditor.Lib.Services.SemanticKernel;

public sealed class SemanticKernelEngine
{
    private readonly HttpClient httpClient;

    private readonly ILoggerFactory loggerFactory;

    public SemanticKernelEngine(HttpClient? httpClient = null, ILoggerFactory? loggerFactory = null)
    {
        this.httpClient = httpClient ?? CreateHttpClient();
        this.loggerFactory = loggerFactory ?? CreateDefaultLoggerFactory();
    }

    public async Task<SemanticKernelContext> RunAsync(string markdown, string userCommand)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(markdown);
        ArgumentException.ThrowIfNullOrWhiteSpace(userCommand);

        var repository = new MarkdownDocumentRepository();
        var document = repository.LoadFromMarkdown(markdown);

        var documentContext = new DocumentContext(document);

        var builder = Kernel.CreateBuilder();

        builder.Services.AddSingleton(documentContext);
        builder.Services.AddSingleton(documentContext.TargetSetContext);
        builder.Services.AddSingleton(documentContext.SessionStore);
        builder.Services.AddSingleton<CursorAgentRuntime>();
        builder.Services.AddSingleton<ILoggerFactory>(loggerFactory);
        builder.Services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        builder.Services.AddSingleton<FunctionInvocationLoggingFilter>();

        var modelId = Environment.GetEnvironmentVariable("LLM_MODEL") ?? "gpt-oss:120b-cloud";
        var baseUrl = Environment.GetEnvironmentVariable("LLM_BASE_URL") ?? "http://localhost:11434";
        var apiKey = Environment.GetEnvironmentVariable("LLM_API_KEY") ?? "ollama";

        var endpoint = baseUrl.TrimEnd('/');
        if (!endpoint.EndsWith("/v1"))
        {
            endpoint += "/v1";
        }

        builder.AddOpenAIChatCompletion(
            modelId: modelId,
            apiKey: apiKey,
            endpoint: new Uri(endpoint),
            httpClient: httpClient);

        builder.Plugins.AddFromType<CursorAgentPlugin>();

        var kernel = builder.Build();
        var functionLogger = kernel.Services.GetRequiredService<FunctionInvocationLoggingFilter>();
        kernel.FunctionInvocationFilters.Add(functionLogger);
        kernel.AutoFunctionInvocationFilters.Add(functionLogger);

        var logger = loggerFactory.CreateLogger<SemanticKernelEngine>();
        logger.LogInformation("Kernel built with model {ModelId} at {Endpoint}", modelId, endpoint);

        var chatService = kernel.GetRequiredService<IChatCompletionService>();
        var history = new ChatHistory();
        history.AddSystemMessage(
            """
            You are a QA assistant for a markdown book that is already loaded into the available kernel functions. Always use the tools to inspect the document instead of world knowledge. Preferred workflow:
            - For location questions, call run_cursor_agent with a forward cursor (maxElements 50, maxBytes 32768, includeContent true) in FirstMatch mode with a precise task and include the pointerLabel (and pointer) in the summary. Treat headings as metadata; when the user asks about mentions in the text, return the first paragraph/list item that matches, not the heading.
            - Never invent content; if the book lacks the answer, reply that it is not found in the document.
            - Stop as soon as you have the relevant paragraph; do not iterate over the entire cursor without a reason.
            Return the final answer in Russian and include the semantic pointer when applicable.
            """);
        history.AddUserMessage(userCommand);

        logger.LogInformation("User command: {UserCommand}", userCommand);

        var executionSettings = new OpenAIPromptExecutionSettings
        {
            ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions,
            Temperature = 0
        };

        var result = await chatService.GetChatMessageContentsAsync(history, executionSettings, kernel);

        var answer = result.FirstOrDefault()?.Content ?? string.Empty;
        logger.LogInformation("LLM answer: {Answer}", TruncateForLog(answer));

        var context = new SemanticKernelContext
        {
            LastCommand = userCommand,
            LastAnswer = answer
        };
        context.UserMessages.Add(answer);

        return context;
    }

    private static HttpClient CreateHttpClient()
    {
        var ignoreSsl = Environment.GetEnvironmentVariable("LLM_IGNORE_SSL_ERRORS") == "true";
        var handler = new HttpClientHandler();
        if (ignoreSsl)
        {
            handler.ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true;
        }

        HttpMessageHandler finalHandler = handler;

        var user = Environment.GetEnvironmentVariable("LLM_USERNAME");
        var password = Environment.GetEnvironmentVariable("LLM_PASSWORD");

        if (!string.IsNullOrEmpty(password))
        {
            finalHandler = new BasicAuthHandler(user ?? string.Empty, password, handler);
        }

        return new HttpClient(finalHandler)
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
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

    private static ILoggerFactory CreateDefaultLoggerFactory()
    {
        return LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Debug);
        });
    }

    private static string TruncateForLog(string text, int maxLength = 4000)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
        {
            return text;
        }

        return text[..maxLength] + $"... (+{text.Length - maxLength} chars)";
    }
}
