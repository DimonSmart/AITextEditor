using System.ComponentModel;
using System.Text.Json;
using AiTextEditor.Lib.Common;
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
    [Description("Runs the chat-driven cursor agent. The cursor name must be provided in the 'cursorName' argument.")]
    public async Task<string> RunChatCursorAgent(
        [Description("Task description for the agent.")] string taskDescription,
        [Description("Cursor name to read from.")] string cursorName,
        [Description("Max evidence capacity.")] int maxEvidenceCount = 1,
        [Description("Optional Pointer after which the cursor should start. null - from the beginning.")] string? startAfterPointer = null)
        
    {
        var request = new CursorAgentRequest(taskDescription, startAfterPointer, cursorName, maxEvidenceCount);
        var result = await runtime.RunAsync(request);
        logger.LogInformation("run_chat_cursor_agent: cursor={Cursor}", cursorName);
        return JsonSerializer.Serialize(new { success = true, result }, SerializationOptions.RelaxedCompact);
    }
}
