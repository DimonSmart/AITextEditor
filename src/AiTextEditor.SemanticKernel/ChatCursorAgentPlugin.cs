using System.ComponentModel;
using System.Text.Json;
using AiTextEditor.Lib.Model;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace AiTextEditor.SemanticKernel;

public sealed class ChatCursorAgentPlugin(
    ChatCursorAgentRuntime runtime,
    ILogger<ChatCursorAgentPlugin> logger)
{
    private readonly ChatCursorAgentRuntime runtime = runtime;
    private readonly ILogger<ChatCursorAgentPlugin> logger = logger;

    [KernelFunction("run_chat_cursor_agent")]
    [Description("Runs the chat-driven cursor agent. The cursor name must be provided in the 'context' argument.")]
    public async Task<string> RunChatCursorAgent(
        [Description("Task description for the agent.")] string taskDescription,
        [Description("Cursor name to read from.")] string cursorName,
        [Description("Pointer after which the cursor should start.")] string? startAfterPointer = null,
        [Description("Optional evidence cap.")] int? maxEvidenceCount = null)
    {
        var request = new CursorAgentRequest(taskDescription, startAfterPointer, cursorName, maxEvidenceCount);
        var result = await runtime.RunAsync(request);
        logger.LogInformation("run_chat_cursor_agent: cursor={Cursor}", cursorName);
        return JsonSerializer.Serialize(new { success = true, result });
    }
}
