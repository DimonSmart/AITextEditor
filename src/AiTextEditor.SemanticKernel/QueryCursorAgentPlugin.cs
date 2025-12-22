using System.ComponentModel;
using System.Threading.Tasks;
using System.Text.Encodings.Web;
using System.Text.Json;
using AiTextEditor.Lib.Model;
using AiTextEditor.Lib.Services.SemanticKernel;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace AiTextEditor.SemanticKernel;

public sealed class QueryCursorAgentPlugin(
    IQueryCursorAgentRuntime cursorAgentRuntime,
    ILogger<QueryCursorAgentPlugin> logger)
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly IQueryCursorAgentRuntime cursorAgentRuntime = cursorAgentRuntime;
    private readonly ILogger<QueryCursorAgentPlugin> logger = logger;

    [KernelFunction("run_query_cursor_agent")]
    [Description("Run a cursor agent against an existing query cursor.")]
    public async Task<string> RunQueryCursorAgent(
        [Description("Cursor name returned by create_query_cursor.")] string cursorName,
        [Description("Natural language task for the agent.")] string taskDescription,
        [Description("Pointer after which the cursor should start.")] string? startAfterPointer = null,
        [Description("Context from previous run to resume.")] string? context = null)
    {
        var request = new CursorAgentRequest(taskDescription, startAfterPointer, context);
        var result = await cursorAgentRuntime.RunAsync(cursorName, request);

        logger.LogInformation("run_query_cursor_agent: cursor={Cursor}, success={Success}", cursorName, result.Success);

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
