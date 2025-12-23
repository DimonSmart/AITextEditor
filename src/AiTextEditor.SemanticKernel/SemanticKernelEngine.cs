using AiTextEditor.Lib.Model;
using AiTextEditor.Lib.Services;
using AiTextEditor.Lib.Services.SemanticKernel;
using AiTextEditor.Lib.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using System.Linq;
using System.Net.Http.Headers;

namespace AiTextEditor.SemanticKernel;

public sealed class SemanticKernelEngine
{
    private readonly HttpClient httpClient;

    private readonly ILoggerFactory loggerFactory;

    public SemanticKernelEngine(HttpClient? httpClient = null, ILoggerFactory? loggerFactory = null)
    {
        this.loggerFactory = loggerFactory ?? CreateDefaultLoggerFactory();
        this.httpClient = httpClient ?? CreateHttpClient(this.loggerFactory);
    }

    public async Task<SemanticKernelContext> RunAsync(string markdown, string userCommand)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(markdown);
        ArgumentException.ThrowIfNullOrWhiteSpace(userCommand);

        var context = new SemanticKernelContext
        {
            LastCommand = userCommand
        };

        var repository = new MarkdownDocumentRepository();
        var document = repository.LoadFromMarkdown(markdown);

        var documentContext = new DocumentContext(document);

        var mcpServer = new EditorSession();
        mcpServer.LoadDefaultDocument(markdown);

        var builder = Kernel.CreateBuilder();

        builder.Services.AddSingleton<IDocumentContext>(documentContext);
        builder.Services.AddSingleton<CursorAgentLimits>();
        builder.Services.AddSingleton<ICursorAgentPromptBuilder, CursorAgentPromptBuilder>();
        builder.Services.AddSingleton<ICursorAgentResponseParser, CursorAgentResponseParser>();
        builder.Services.AddSingleton<ICursorEvidenceCollector, CursorEvidenceCollector>();
        builder.Services.AddSingleton<ICursorAgentRuntime, CursorAgentRuntime>();
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

        //builder.Plugins.AddFromType<CursorAgentPlugin>();
        //var mcpPlugin = new McpServerPlugin(mcpServer, context, loggerFactory.CreateLogger<McpServerPlugin>());
        //builder.Plugins.AddFromObject(mcpPlugin, "mcp");

        var kernel = builder.Build();
        var functionLogger = kernel.Services.GetRequiredService<FunctionInvocationLoggingFilter>();
        kernel.FunctionInvocationFilters.Add(functionLogger);
        kernel.AutoFunctionInvocationFilters.Add(functionLogger);

        var cursorAgentRuntime = kernel.Services.GetRequiredService<ICursorAgentRuntime>();
        var limits = kernel.Services.GetRequiredService<CursorAgentLimits>();
        var cursorRegistry = new CursorRegistry();

        var editorPlugin = new EditorPlugin(
            mcpServer,
            context,
            loggerFactory.CreateLogger<EditorPlugin>());
        kernel.Plugins.AddFromObject(editorPlugin, "editor");

        var cursorPlugin = new CursorPlugin(
            mcpServer,
            cursorRegistry,
            limits,
            loggerFactory.CreateLogger<CursorPlugin>());
        kernel.Plugins.AddFromObject(cursorPlugin, "cursor");

        var agentPlugin = new AgentPlugin(
            cursorRegistry,
            cursorAgentRuntime,
            limits,
            loggerFactory.CreateLogger<AgentPlugin>());
        kernel.Plugins.AddFromObject(agentPlugin, "agent");

        var keywordCursorRegistry = new KeywordCursorRegistry(
            documentContext,
            limits,
            cursorRegistry,
            loggerFactory.CreateLogger<KeywordCursorRegistry>());

        var keywordCursorCreationPlugin = new KeywordCursorCreationPlugin(
            keywordCursorRegistry,
            loggerFactory.CreateLogger<KeywordCursorCreationPlugin>());
        kernel.Plugins.AddFromObject(keywordCursorCreationPlugin, "keyword_cursor");

        var logger = loggerFactory.CreateLogger<SemanticKernelEngine>();
        logger.LogInformation("Kernel built with model {ModelId} at {Endpoint}", modelId, endpoint);

        var chatService = kernel.GetRequiredService<IChatCompletionService>();
        var history = new ChatHistory();
        history.AddSystemMessage(
            """
            You are a QA assistant for a markdown book. Use tools to inspect the document.
            Workflow:
            - For location questions, create a cursor (e.g. name="search_cursor"), then iterate using `run_agent`.
            - For specific keyword search, prefer `create_keyword_cursor` over generic `create_cursor`.
            - For multi-paragraph concepts (like dialogue), use a VERY BROAD filter (e.g. "All paragraphs") to ensure you don't miss anything. Do NOT filter by character names.
            - CRITICAL: 'run_agent' scans ONLY ONE BATCH (10-20 items). It returns "Status: More items available" if there is more text.
            - IF 'run_agent' returns "not_found" AND "Status: More items available":
              YOU MUST CALL 'run_agent' AGAIN with the SAME cursorName.
              DO NOT STOP. REPEAT 'run_agent' UNTIL you find the answer OR status is "cursor_complete".
            - Stop ONLY when decision is "done" OR "cursor_complete" OR ("not_found" AND hasMore=false).
            - Be careful with counting mentions: a single paragraph may contain MULTIPLE mentions. Read the text carefully.
            - CHECK PREVIOUS EVIDENCE: The answer might be in a paragraph found in a previous step.
            - DIALOGUE: A sequence of paragraphs where different characters speak IS A DIALOGUE. Report it.
            - DIALOGUE FORMATS: Look for 'Name: Text', 'Name said, "Text"', OR paragraphs starting with dashes (–/-) where context implies different speakers.
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

        context.LastAnswer = answer;
        context.UserMessages.Add(answer);

        return context;
    }

    private static HttpClient CreateHttpClient(ILoggerFactory loggerFactory)
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

        var logger = loggerFactory.CreateLogger<LlmRequestLoggingHandler>();
        finalHandler = new LlmRequestLoggingHandler(logger, finalHandler);

        return new HttpClient(finalHandler)
        {
            Timeout = TimeSpan.FromMinutes(20)
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
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddConsole();
            builder.AddFilter<ConsoleLoggerProvider>(level => level >= LogLevel.Debug);
            builder.AddFilter("Microsoft.SemanticKernel.Connectors.OpenAI.OpenAIChatCompletionService", LogLevel.Information);
            builder.AddProvider(new SimpleFileLoggerProvider("llm_debug.log"));
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
