using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiTextEditor.Core.Infrastructure;
using AiTextEditor.Core.Model;
using AiTextEditor.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace AiTextEditor.Agent;

public sealed class AgenticWorkflowEngine
{
    private static readonly JsonSerializerOptions ResponseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    };

    private readonly HttpClient httpClient;
    private readonly string? fixedDossiersId;
    private readonly ILoggerFactory loggerFactory;
    private readonly string? modelOverride;

    public AgenticWorkflowEngine(HttpClient? httpClient = null, ILoggerFactory? loggerFactory = null, string? fixedDossiersId = null, string? modelOverride = null)
    {
        this.loggerFactory = loggerFactory ?? CreateDefaultLoggerFactory();
        this.httpClient = httpClient ?? CreateHttpClient(this.loggerFactory);
        this.fixedDossiersId = fixedDossiersId;
        this.modelOverride = modelOverride;
    }

    public async Task<AgenticWorkflowContext> RunAsync(
        string markdown,
        string userCommand,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(markdown);
        ArgumentException.ThrowIfNullOrWhiteSpace(userCommand);

        var context = new AgenticWorkflowContext
        {
            LastCommand = userCommand
        };

        var repository = new MarkdownDocumentRepository();
        var document = repository.LoadFromMarkdown(markdown);

        var dossierService = new CharacterDossierService(fixedDossiersId);
        var documentContext = new DocumentContext(document, dossierService);
        var cursorRegistry = new CursorRegistry();
        var limits = new CursorAgentLimits();

        var modelId = modelOverride ?? Environment.GetEnvironmentVariable("LLM_MODEL") ?? "gpt-oss:120b-cloud";
        var baseUrl = Environment.GetEnvironmentVariable("LLM_BASE_URL") ?? "http://localhost:11434";
        var apiKey = Environment.GetEnvironmentVariable("LLM_API_KEY") ?? "ollama";
        var endpoint = NormalizeOpenAiEndpoint(baseUrl);

        var logger = loggerFactory.CreateLogger<AgenticWorkflowEngine>();
        logger.LogInformation("Agentic workflow engine built with model {ModelId} at {Endpoint}", modelId, endpoint);
        logger.LogInformation("User command: {UserCommand}", userCommand);

        var modelClient = AgenticFrameworkModelClient.CreateOpenAiCompatible(
            httpClient,
            modelId,
            new Uri(endpoint),
            apiKey,
            loggerFactory);

        var extractionClient = new AgenticCharacterExtractionModelClient(
            modelClient,
            loggerFactory.CreateLogger<AgenticCharacterExtractionModelClient>());

        var generator = new CharacterDossiersGenerator(
            documentContext,
            dossierService,
            limits,
            loggerFactory.CreateLogger<CharacterDossiersGenerator>(),
            extractionClient);

        var workflowRunner = new CharacterBibleWorkflowRunner(generator, loggerFactory);
        var dossiersPlugin = new CharacterDossiersPlugin(
            generator,
            cursorRegistry,
            dossierService,
            limits,
            loggerFactory.CreateLogger<CharacterDossiersPlugin>(),
            workflowRunner);

        if (IsCharacterDossiersCommand(userCommand))
        {
            await dossiersPlugin.GenerateCharacterDossiersAsync(cancellationToken);
            var payload = dossiersPlugin.GetCharacterDossiers();
            return Complete(context, JsonSerializer.Serialize(payload, ResponseJsonOptions));
        }

        var cursorPlugin = new CursorPlugin(
            documentContext,
            cursorRegistry,
            limits,
            loggerFactory.CreateLogger<CursorPlugin>());

        var cursorRuntime = new CursorAgentRuntime(
            cursorRegistry,
            new AgenticCursorAgentModelClient(modelClient),
            new CursorAgentPromptBuilder(limits),
            new CursorEvidenceCollector(),
            limits,
            loggerFactory.CreateLogger<CursorAgentRuntime>());

        var cursorName = cursorPlugin.CreateFullScanCursor(includeHeadings: false);
        var result = await cursorRuntime.RunAsync(
            cursorName,
            new CursorAgentRequest(userCommand, StartAfterPointer: null, Context: null, MaxEvidenceCount: limits.DefaultMaxFound),
            cancellationToken);

        return Complete(context, FormatCursorAgentAnswer(result));
    }

    private static AgenticWorkflowContext Complete(AgenticWorkflowContext context, string answer)
    {
        context.LastAnswer = answer;
        context.UserMessages.Add(answer);
        return context;
    }

    private static bool IsCharacterDossiersCommand(string userCommand)
    {
        var normalized = userCommand.ToLowerInvariant();
        return normalized.Contains("character_dossiers.generate_character_dossiers", StringComparison.Ordinal)
               || normalized.Contains("generate_character_dossiers", StringComparison.Ordinal)
               || normalized.Contains("досье персона", StringComparison.Ordinal)
               || (normalized.Contains("библи", StringComparison.Ordinal) && normalized.Contains("персона", StringComparison.Ordinal));
    }

    private static string FormatCursorAgentAnswer(CursorAgentResult result)
    {
        if (!result.Success)
        {
            return result.Summary ?? "not_found";
        }

        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(result.Summary))
        {
            builder.AppendLine(result.Summary);
            builder.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(result.Excerpt))
        {
            builder.AppendLine(result.Excerpt);
            builder.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(result.WhyThis))
        {
            builder.AppendLine(result.WhyThis);
            builder.AppendLine();
        }

        builder.Append("Pointer: ");
        builder.Append(result.SemanticPointerFrom);
        return builder.ToString();
    }

    private static string NormalizeOpenAiEndpoint(string baseUrl)
    {
        var endpoint = baseUrl.TrimEnd('/');
        if (!endpoint.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            endpoint += "/v1";
        }

        return endpoint;
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
            var byteArray = Encoding.UTF8.GetBytes($"{user}:{password}");
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
            builder.AddFilter("AiTextEditor.Agent.CursorAgentRuntime", LogLevel.Information);
            var logPath = SimpleFileLoggerProvider.CreateTimestampedPath("llm_debug.log");
            builder.AddProvider(new SimpleFileLoggerProvider(logPath));
        });
    }
}
