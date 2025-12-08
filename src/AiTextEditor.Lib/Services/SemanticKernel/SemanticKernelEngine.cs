using System.Text.RegularExpressions;
using AiTextEditor.Lib.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AiTextEditor.Lib.Services.SemanticKernel;

public sealed class SemanticKernelEngine
{
    private readonly HttpClient httpClient;

    public SemanticKernelEngine(HttpClient httpClient)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<SemanticKernelContext> RunPointerQuestionAsync(string markdown, string userCommand, string? documentId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(markdown);
        ArgumentException.ThrowIfNullOrWhiteSpace(userCommand);

        var server = new McpServer();
        var document = server.LoadDefaultDocument(markdown, documentId);
        var context = CreateKernelContext(userCommand);
        var kernel = CreateKernel(server, context);

        var items = server.GetItems(document.Id);
        var formattedItems = string.Join("\n", items.Select(item => $"{item.Pointer.SemanticNumber}: {item.Text}"));

        var chatService = kernel.GetRequiredService<IChatCompletionService>();
        var history = new ChatHistory();
        history.AddSystemMessage("You are a Markdown MCP assistant. Use the MCP functions to inspect the document and answer with the exact pointer for the requested content.");
        history.AddSystemMessage($"Document items:\n{formattedItems}");
        history.AddUserMessage(userCommand);

        var responses = await chatService.GetChatMessageContentsAsync(history, new PromptExecutionSettings(), kernel);
        var answer = responses.FirstOrDefault()?.Content ?? string.Empty;

        context.LastAnswer = answer;
        var pointer = ExtractPointer(answer);
        if (!string.IsNullOrWhiteSpace(pointer))
        {
            var targetSet = server.CreateTargetSet(new[] { FindItemIndex(items, pointer) }, userCommand, label: "user-request");
            context.LastTargetSet = targetSet;
            context.UserMessages.Add(answer);
        }

        return context;
    }

    public async Task<SemanticKernelContext> SummarizeChapterAsync(string markdown, string userCommand, string? documentId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(markdown);
        ArgumentException.ThrowIfNullOrWhiteSpace(userCommand);

        var server = new McpServer();
        var document = server.LoadDefaultDocument(markdown, documentId);
        var context = CreateKernelContext(userCommand);
        var kernel = CreateKernel(server, context);

        var chatService = kernel.GetRequiredService<IChatCompletionService>();
        var targetSet = CaptureChapterTargets(server, document, userCommand);
        context.LastTargetSet = targetSet;

        var response = await CreateLlmSummaryAsync(chatService, kernel, targetSet, userCommand);
        context.LastAnswer = response.Text;
        context.UserMessages.Add(response.Text);

        return context;
    }

    private SemanticKernelContext CreateKernelContext(string userCommand)
    {
        return new SemanticKernelContext
        {
            LastCommand = userCommand
        };
    }

    private Kernel CreateKernel(McpServer server, SemanticKernelContext context)
    {
        var lamaClient = new LamaClient(httpClient);
        var builder = Kernel.CreateBuilder();

        builder.Services.AddSingleton(lamaClient);
        builder.Services.AddSingleton<IChatCompletionService, LlamaSemanticKernelChatService>();
        builder.Plugins.AddFromObject(new McpServerPlugin(server, context));

        return builder.Build();
    }

    private static string ExtractPointer(string answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
        {
            return string.Empty;
        }

        var match = Regex.Match(answer, @"\d+(?:\.\d+)*\.p\d+", RegexOptions.IgnoreCase);
        return match.Success ? match.Value : string.Empty;
    }

    private static int FindItemIndex(IReadOnlyList<LinearItem> items, string pointer)
    {
        var match = items.FirstOrDefault(item => string.Equals(item.Pointer.SemanticNumber, pointer, StringComparison.OrdinalIgnoreCase));
        if (match == null)
        {
            throw new InvalidOperationException($"Pointer '{pointer}' was not found in the document.");
        }

        return match.Index;
    }

    private static TargetSet CaptureChapterTargets(McpServer server, LinearDocument document, string userCommand)
    {
        var items = server.GetItems(document.Id);
        var headings = items.Where(item => item.Type == LinearItemType.Heading).ToList();
        var chapterNumber = ExtractChapterNumber(userCommand, headings.Count);
        var start = headings[chapterNumber - 1].Index + 1;
        var end = chapterNumber < headings.Count ? headings[chapterNumber].Index : items.Count;
        var chapterItems = items.Skip(start).Take(end - start).ToList();

        var targetSet = server.CreateTargetSet(chapterItems.Select(item => item.Index), userCommand, label: $"chapter-{chapterNumber}-summary");
        return targetSet;
    }

    private static int ExtractChapterNumber(string userCommand, int headingCount)
    {
        var lower = userCommand.ToLowerInvariant();
        if (lower.Contains("вторая", StringComparison.OrdinalIgnoreCase) || lower.Contains("second", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        if (lower.Contains("первая", StringComparison.OrdinalIgnoreCase) || lower.Contains("first", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        var digit = lower.FirstOrDefault(char.IsDigit);
        if (digit != default && int.TryParse(digit.ToString(), out var parsed) && parsed > 0 && parsed <= headingCount)
        {
            return parsed;
        }

        throw new InvalidOperationException("Could not determine requested chapter number from command.");
    }

    private async Task<LamaChatResponse> CreateLlmSummaryAsync(IChatCompletionService chatService, Kernel kernel, TargetSet targetSet, string userCommand)
    {
        var builder = new ChatHistory();
        var combinedTargets = string.Join("\n", targetSet.Targets.Select(target => target.Text));

        builder.AddSystemMessage("You are summarizing a Markdown chapter for the user based on selected targets.");
        builder.AddSystemMessage($"Targets:\n{combinedTargets}");
        builder.AddUserMessage(userCommand);

        var result = await chatService.GetChatMessageContentsAsync(builder, new PromptExecutionSettings(), kernel);
        var content = result.FirstOrDefault();
        if (content == null)
        {
            throw new InvalidOperationException("LLM did not return a response.");
        }

        return new LamaChatResponse(((LlamaSemanticKernelChatService)chatService).ModelId, content.Content, null);
    }
}
