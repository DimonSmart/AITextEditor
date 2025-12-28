using System.ComponentModel;
using System.Text.Encodings.Web;
using System.Text.Json;
using AiTextEditor.Lib.Model;
using AiTextEditor.Lib.Services.SemanticKernel;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace AiTextEditor.SemanticKernel;

public class CursorAgentPlugin(
    ICursorAgentRuntime cursorAgentRuntime,
    ILogger<CursorAgentPlugin> logger)
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly ICursorAgentRuntime cursorAgentRuntime = cursorAgentRuntime;
    private readonly ILogger<CursorAgentPlugin> logger = logger;

    [KernelFunction("run_cursor_agent")]
    [Description("Launch a cursor agent with a concise system prompt.")]
    public async Task<string> RunCursorAgent(
        [Description("Cursor name returned by any create_cursor_ function.")] string cursorName,
        [Description("Natural language task for the agent.")] string taskDescription,
        [Description("Pointer after which the cursor should start.")] string? startAfterPointer = null,
        [Description("Context from previous run to resume.")] string? context = null)
    {
        var request = new CursorAgentRequest(taskDescription, startAfterPointer, context);
        var result = await cursorAgentRuntime.RunAsync(cursorName, request);

        logger.LogInformation("run_cursor_agent: cursor={Cursor}, success={Success}", cursorName, result.Success);

        // Create a lightweight result for the LLM to avoid token limit issues and distractions
        var lightweightResult = new
        {
            result.Success,
            result.Summary,
            result.NextAfterPointer,
            result.CursorComplete,
            result.SemanticPointerFrom,
            result.Excerpt,
            result.WhyThis,
            result.Evidence
        };

        return JsonSerializer.Serialize(lightweightResult, SerializerOptions);
    }
}
