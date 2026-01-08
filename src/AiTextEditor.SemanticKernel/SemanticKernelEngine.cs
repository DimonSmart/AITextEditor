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
    private readonly string? fixedDossiersId;

    private readonly ILoggerFactory loggerFactory;

    public SemanticKernelEngine(HttpClient? httpClient = null, ILoggerFactory? loggerFactory = null, string? fixedDossiersId = null)
    {
        this.loggerFactory = loggerFactory ?? CreateDefaultLoggerFactory();
        this.httpClient = httpClient ?? CreateHttpClient(this.loggerFactory);
        this.fixedDossiersId = fixedDossiersId;
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

        var dossierService = new CharacterDossierService(fixedDossiersId);
        var documentContext = new DocumentContext(document, dossierService);

        var mcpServer = new EditorSession(
            repository,
            new LinearDocumentEditor(),
            new InMemoryTargetSetService(),
            dossierService);
        mcpServer.LoadDefaultDocument(markdown);

        var builder = Kernel.CreateBuilder();

        var cursorRegistry = new CursorRegistry();

        builder.Services.AddSingleton(dossierService);
        builder.Services.AddSingleton<IDocumentContext>(documentContext);
        builder.Services.AddSingleton<ICursorStore>(cursorRegistry);
        builder.Services.AddSingleton<CursorAgentLimits>();
        builder.Services.AddSingleton<ICursorAgentPromptBuilder, CursorAgentPromptBuilder>();
        builder.Services.AddSingleton<ICursorAgentResponseParser, CursorAgentResponseParser>();
        builder.Services.AddSingleton<ICursorEvidenceCollector, CursorEvidenceCollector>();
        builder.Services.AddSingleton<ICursorAgentRuntime, CursorAgentRuntime>();
        builder.Services.AddSingleton<CharacterDossiersGenerator>(sp =>
            new CharacterDossiersGenerator(
                sp.GetRequiredService<IDocumentContext>(),
                sp.GetRequiredService<CharacterDossierService>(),
                sp.GetRequiredService<CursorAgentLimits>(),
                sp.GetRequiredService<ILogger<CharacterDossiersGenerator>>(),
                sp.GetRequiredService<IChatCompletionService>()));
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

        var kernel = builder.Build();
        var functionLogger = kernel.Services.GetRequiredService<FunctionInvocationLoggingFilter>();
        kernel.FunctionInvocationFilters.Add(functionLogger);
        kernel.AutoFunctionInvocationFilters.Add(functionLogger);

        var chatService = kernel.GetRequiredService<IChatCompletionService>();
        var limits = kernel.Services.GetRequiredService<CursorAgentLimits>();

        var editorPlugin = new EditorPlugin(
            mcpServer,
            context,
            loggerFactory.CreateLogger<EditorPlugin>());
        kernel.Plugins.AddFromObject(editorPlugin, "editor");

        var cursorPlugin = new CursorPlugin(
            documentContext,
            cursorRegistry,
            limits,
            loggerFactory.CreateLogger<CursorPlugin>());
        kernel.Plugins.AddFromObject(cursorPlugin, "cursor");

        var cursorAgentPlugin = new CursorAgentPlugin(
            kernel.Services.GetRequiredService<ICursorAgentRuntime>(),
            loggerFactory.CreateLogger<CursorAgentPlugin>());
        kernel.Plugins.AddFromObject(cursorAgentPlugin, "cursor_agent");

        var dossiersGenerator = kernel.Services.GetRequiredService<CharacterDossiersGenerator>();
        var characterDossiersPlugin = new CharacterDossiersPlugin(
            dossiersGenerator,
            cursorRegistry,
            dossierService,
            limits,
            loggerFactory.CreateLogger<CharacterDossiersPlugin>());
        kernel.Plugins.AddFromObject(characterDossiersPlugin, "character_dossiers");

        var logger = loggerFactory.CreateLogger<SemanticKernelEngine>();
        logger.LogInformation("Kernel built with model {ModelId} at {Endpoint}", modelId, endpoint);

        var history = new ChatHistory();
        history.AddSystemMessage(
            $$"""
            You are a Editor assistant for a markdown book. Use tools to inspect the document.
            Info:
            - Книга на русском языке.
            - Книга детская.
            
            Terms:
            - Semantic pointer, pointer to the book paragraph in for like '1.1.1.p1'.
              It is returned by cursors.
              If you asked to point in a particular place in the book - repospond with Semantic Ponter.

            Workflow:
            - Understand user request, use tools provided to respond.`.
            - If task could be done with keyword based search - prefer keyword cursor over llm cursor.
              Choose the most relevant keywords for the question;
            - Для keyword cursor передавай ключевые слова в начальной форме; склонения и формы будут нормализованы системой.
            - For multi-paragraph concepts, use a fullscan cursor. 
            - For long scans, consider running cursor_agent-run_cursor_agent after creating a cursor.
            - Be careful with counting mentions: a single paragraph may contain MULTIPLE mentions. Read the text carefully.
            - CHECK PREVIOUS EVIDENCE: The answer might be in a paragraph found in a previous cursor portion.
            Return the final answer in the same language as the user question and include the semantic pointer when applicable.
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
            builder.AddFilter("AiTextEditor.SemanticKernel.CursorAgentRuntime", LogLevel.Information);
            builder.AddFilter("Microsoft.SemanticKernel.Connectors.OpenAI.OpenAIChatCompletionService", LogLevel.Information);
            builder.AddFilter("Microsoft.SemanticKernel.KernelFunction", LogLevel.Debug);
            var logPath = SimpleFileLoggerProvider.CreateTimestampedPath("llm_debug.log");
            builder.AddProvider(new SimpleFileLoggerProvider(logPath));
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
