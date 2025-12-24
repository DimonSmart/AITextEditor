using System.Linq;
using System.Text;
using AiTextEditor.Lib.Model;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace AiTextEditor.SemanticKernel;

public sealed class ChatCursorAgentRuntime
{
    private readonly Kernel kernel;
    private readonly CursorRegistry registry;
    private readonly IChatCompletionService chatService;
    private readonly FunctionCallAwareChatHistoryCompressor compressor;
    private readonly CursorAgentLimits limits;
    private readonly ILogger<ChatCursorAgentRuntime> logger;

    public ChatCursorAgentRuntime(
        Kernel kernel,
        CursorRegistry registry,
        IChatCompletionService chatService,
        FunctionCallAwareChatHistoryCompressor compressor,
        CursorAgentLimits limits,
        ILogger<ChatCursorAgentRuntime> logger)
    {
        this.kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        this.chatService = chatService ?? throw new ArgumentNullException(nameof(chatService));
        this.compressor = compressor ?? throw new ArgumentNullException(nameof(compressor));
        this.limits = limits ?? throw new ArgumentNullException(nameof(limits));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<string> RunAsync(CursorAgentRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TaskDescription);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Context);

        if (!registry.TryGetCursor(request.Context!, out var cursor) || cursor == null)
        {
            throw new InvalidOperationException($"Cursor '{request.Context}' not found.");
        }

        var history = new ChatHistory();
        history.AddSystemMessage(BuildSystemPrompt());
        history.AddUserMessage(BuildTaskMessage(request));

        var settings = new OpenAIPromptExecutionSettings
        {
            ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions,
            Temperature = 0
        };

        string? finalAnswer = null;
        for (var step = 0; step < limits.DefaultMaxSteps; step++)
        {
            compressor.Trim(history);

            var responses = await chatService.GetChatMessageContentsAsync(history, settings, kernel, cancellationToken);
            if (responses.Count == 0)
            {
                break;
            }

            foreach (var message in responses)
            {
                history.Add(message);
                if (message.Role == AuthorRole.Assistant && !ContainsFunctionCall(message))
                {
                    finalAnswer = message.Content ?? string.Empty;
                }
            }

            if (finalAnswer != null)
            {
                break;
            }
        }

        if (finalAnswer == null)
        {
            logger.LogWarning("chat_cursor_agent: reached max steps without final answer");
            finalAnswer = "Не удалось завершить обработку курсора: превышен лимит шагов.";
        }

        return finalAnswer;
    }

    private static bool ContainsFunctionCall(ChatMessageContent message)
    {
        return message.Items.Any(item => item is FunctionCallContent);
    }

    private static string BuildTaskMessage(CursorAgentRequest request)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Task:");
        builder.AppendLine(request.TaskDescription);
        builder.AppendLine();
        builder.AppendLine($"cursorName: {request.Context}");
        if (!string.IsNullOrWhiteSpace(request.StartAfterPointer))
        {
            builder.AppendLine($"startAfterPointer: {request.StartAfterPointer}");
        }

        if (request.MaxEvidenceCount.HasValue)
        {
            builder.AppendLine($"maxEvidenceCount: {request.MaxEvidenceCount.Value}");
        }

        return builder.ToString();
    }

    private static string BuildSystemPrompt()
    {
        return """
You are ChatCursorAgent. You must read a document via the tool read_cursor_batch.

Rules:
- Always call read_cursor_batch to fetch the next batch until it returns hasMore=false or you have enough evidence.
- Do NOT fabricate evidence. Use only tool outputs.
- Keep the conversation concise; summarize intermediate findings instead of repeating raw text.
- When you have enough information, respond with a concise answer in Russian.
- Do not include JSON or code fences in the final message.
""";
    }
}
