using AiTextEditor.Lib.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace AiTextEditor.Lib.Services.SemanticKernel;

public sealed class SemanticKernelEngine
{
    private readonly HttpClient httpClient;

    public SemanticKernelEngine(HttpClient httpClient)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<SemanticKernelContext> RunAsync(string markdown, string userCommand)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(markdown);
        ArgumentException.ThrowIfNullOrWhiteSpace(userCommand);

        // 1. Load Domain Model
        var repository = new MarkdownDocumentRepository();
        var document = repository.LoadFromMarkdown(markdown);

        // 2. Create Shared Context
        var documentContext = new DocumentContext(document);

        // 3. Build Kernel
        var builder = Kernel.CreateBuilder();
        
        // Register Services
        builder.Services.AddSingleton(documentContext);

        // Configure OpenAI Connector for Ollama
        var modelId = Environment.GetEnvironmentVariable("LLM_MODEL") ?? "gpt-oss:120b-cloud";
        var baseUrl = Environment.GetEnvironmentVariable("LLM_BASE_URL") ?? "http://localhost:11434";
        var apiKey = Environment.GetEnvironmentVariable("LLM_API_KEY") ?? "ollama";

        // Ensure the endpoint ends with /v1 for OpenAI compatibility
        var endpoint = baseUrl.TrimEnd('/');
        if (!endpoint.EndsWith("/v1"))
        {
            endpoint += "/v1";
        }

        builder.AddOpenAIChatCompletion(
            modelId: modelId,
            apiKey: apiKey,
            endpoint: new Uri(endpoint));
        
        // Register Plugins
        builder.Plugins.AddFromType<NavigationPlugin>();

        var kernel = builder.Build();

        // 4. Execute
        var chatService = kernel.GetRequiredService<IChatCompletionService>();
        var history = new ChatHistory();
        history.AddSystemMessage("You are a helpful assistant. Use the available tools to answer the user's question.");
        history.AddUserMessage(userCommand);

        var executionSettings = new OpenAIPromptExecutionSettings 
        { 
            ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions 
        };

        var result = await chatService.GetChatMessageContentsAsync(history, executionSettings, kernel);
        var answer = result.FirstOrDefault()?.Content ?? string.Empty;

        // 5. Return Context (Populate what we can for compatibility/verification)
        var context = new SemanticKernelContext
        {
            LastCommand = userCommand,
            LastAnswer = answer
        };
        context.UserMessages.Add(answer);

        return context;
    }
}
