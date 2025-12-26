using AiTextEditor.Lib.Model;
using AiTextEditor.Lib.Services.SemanticKernel;
using Markdig;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AiTextEditor.SemanticKernel;

public sealed class ChatCursorAgentRuntime
{
    private readonly Kernel kernel;
    private readonly ICursorStore cursorStore;
    private readonly IChatCompletionService chatService;
    private readonly FunctionCallAwareChatHistoryCompressor compressor;
    private readonly CursorAgentLimits limits;
    private readonly ILogger<ChatCursorAgentRuntime> logger;

    public ChatCursorAgentRuntime(
        Kernel kernel,
        ICursorStore cursorStore,
        IChatCompletionService chatService,
        FunctionCallAwareChatHistoryCompressor compressor,
        CursorAgentLimits limits,
        ILogger<ChatCursorAgentRuntime> logger)
    {
        this.kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        this.cursorStore = cursorStore ?? throw new ArgumentNullException(nameof(cursorStore));
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

        if (!cursorStore.TryGetCursor(request.Context!, out var cursor) || cursor == null)
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

                // TODO: Проверь, мы действительно тут можем увидеть вызов функции?
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
            finalAnswer = "chat_cursor_agent: reached max steps without final answer";
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

    // Tightens the schema for small models and repeats the exact tool name to reduce tool-call hallucinations.
    private static string BuildSystemPrompt()
    {
        var builder = new StringBuilder();
        builder.AppendLine("You are ChatCursorAgent. Read the document ONLY via the tool read_cursor_batch.");
        builder.AppendLine("Rules:");
        builder.AppendLine("- Always call read_cursor_batch to fetch the next batch until hasMore=false or you are confident you can answer.");
        builder.AppendLine("- Do NOT fabricate evidence. Use only tool outputs.");
        builder.AppendLine("- Keep the conversation concise; summarize intermediate findings instead of repeating raw text.");
        builder.AppendLine("- Final answer must be in Russian.");
        builder.AppendLine("- Return exactly ONE JSON object as the final message (no code fences, no extra text).");
        builder.AppendLine();
        builder.AppendLine("Output schema (JSON):");
        builder.AppendLine("{");
        builder.AppendLine("  \"answer\": \"...\",");
        builder.AppendLine("  \"pointer\": \"...\"");
        builder.AppendLine("}");
        builder.AppendLine("pointer: copy the pointer from read_cursor_batch if available; otherwise use null or an empty string.");
        return builder.ToString();
    }
}
