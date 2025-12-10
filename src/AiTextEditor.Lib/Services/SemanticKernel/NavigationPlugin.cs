using System;
using System.ComponentModel;
using System.Text.Json;
using System.Threading.Tasks;
using AiTextEditor.Lib.Model;
using AiTextEditor.Lib.Services;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace AiTextEditor.Lib.Services.SemanticKernel;

public class NavigationPlugin(DocumentContext context, CursorQueryExecutor cursorQueryExecutor, ILogger<NavigationPlugin> logger)
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly DocumentContext context = context;
    private readonly CursorQueryExecutor cursorQueryExecutor = cursorQueryExecutor;
    private readonly ILogger<NavigationPlugin> logger = logger;

    [KernelFunction]
    [Description("Creates or resets a named cursor with paging limits for chunked processing.")]
    public string CreateCursor(
        [Description("Cursor name that will be reused in subsequent calls.")] string cursorName,
        [Description("Maximum number of items per portion.")] int maxElements = 20,
        [Description("Maximum byte size per portion.")] int maxBytes = 2048,
        [Description("When true, include raw text in each portion.")] bool includeText = true,
        [Description("Direction of traversal: Forward or Backward.")] string direction = "Forward")
    {
        var cursorDirection = ParseDirection(direction);
        var parameters = new CursorParameters(maxElements, maxBytes, includeText);
        logger.LogInformation("CreateCursor: name={CursorName}, direction={Direction}, maxElements={MaxElements}, maxBytes={MaxBytes}, includeText={IncludeText}", cursorName, cursorDirection, maxElements, maxBytes, includeText);

        context.CursorContext.CreateCursor(cursorName, parameters, cursorDirection);
        var handle = new CursorHandle(cursorName, cursorDirection, parameters.MaxElements, parameters.MaxBytes, parameters.IncludeText);
        return JsonSerializer.Serialize(handle, SerializerOptions);
    }

    [KernelFunction]
    [Description("Resets the default whole-book cursor for the given direction and returns its handle.")]
    public string UseWholeBookCursor(
        [Description("Direction of traversal: Forward or Backward.")] string direction = "Forward",
        [Description("Maximum number of items per portion.")] int maxElements = 20,
        [Description("Maximum byte size per portion.")] int maxBytes = 2048,
        [Description("When true, include raw text in each portion.")] bool includeText = true)
    {
        var cursorDirection = ParseDirection(direction);
        var parameters = new CursorParameters(maxElements, maxBytes, includeText);
        var cursorName = cursorDirection == CursorDirection.Forward
            ? context.CursorContext.EnsureWholeBookForward(parameters)
            : context.CursorContext.EnsureWholeBookBackward(parameters);

        logger.LogInformation("UseWholeBookCursor: name={CursorName}, direction={Direction}", cursorName, cursorDirection);
        var handle = new CursorHandle(cursorName, cursorDirection, parameters.MaxElements, parameters.MaxBytes, parameters.IncludeText);
        return JsonSerializer.Serialize(handle, SerializerOptions);
    }

    [KernelFunction]
    [Description("Fetches the next portion of a cursor as compact JSON with semantic pointers.")]
    public string GetNextPortion([Description("Cursor name returned by CreateCursor/UseWholeBookCursor.")] string cursorName)
    {
        var portion = context.CursorContext.GetNextPortion(cursorName) ?? throw new InvalidOperationException($"Cursor '{cursorName}' is not defined.");
        var snapshot = CursorPortionView.FromPortion(portion);
        logger.LogInformation("GetNextPortion: name={CursorName}, hasMore={HasMore}, count={Count}", cursorName, snapshot.HasMore, snapshot.Items.Count);

        return JsonSerializer.Serialize(snapshot, SerializerOptions);
    }

    [KernelFunction]
    [Description("Executes an instruction over a cursor using paged portions and returns the LLM decision.")]
    public async Task<string> QueryCursor(
        [Description("Cursor name returned by CreateCursor/UseWholeBookCursor.")] string cursorName,
        [Description("Instruction applied to each portion. Ask to return semantic pointers or summaries.")] string instruction)
    {
        var result = await cursorQueryExecutor.ExecuteQueryOverCursorAsync(cursorName, instruction);
        var response = new CursorQueryResponse(cursorName, result.Success, result.Result);
        logger.LogInformation("QueryCursor: name={CursorName}, success={Success}", cursorName, result.Success);

        return JsonSerializer.Serialize(response, SerializerOptions);
    }

    [KernelFunction]
    [Description("Processes every portion of a cursor with the given instruction and aggregates per-portion results.")]
    public async Task<string> MapCursor(
        [Description("Cursor name returned by CreateCursor/UseWholeBookCursor.")] string cursorName,
        [Description("Instruction applied independently to each portion. Keep responses compact.")] string instruction)
    {
        var mapResult = await cursorQueryExecutor.ExecutePortionTasksAsync(cursorName, instruction);
        var response = new CursorMapResponse(cursorName, mapResult.Success, mapResult.Portions);
        logger.LogInformation("MapCursor: name={CursorName}, success={Success}, portions={PortionCount}", cursorName, mapResult.Success, mapResult.Portions.Count);

        return JsonSerializer.Serialize(response, SerializerOptions);
    }

    [KernelFunction]
    [Description("Explains a serialized semantic pointer (SemanticPointer or LinearPointer). Returns JSON with decoded fields.")]
    public string DescribePointer(
        [Description("Serialized pointer JSON returned by cursor queries or portions.")] string pointerJson)
    {
        var pointer = DeserializePointer(pointerJson);
        var description = new PointerDescription(pointer.HeadingTitle, pointer.LineIndex, pointer.CharacterOffset, pointer.Serialize());
        return JsonSerializer.Serialize(description, SerializerOptions);
    }

    private static CursorDirection ParseDirection(string direction)
    {
        if (Enum.TryParse<CursorDirection>(direction, true, out var parsed)) return parsed;
        throw new ArgumentException($"Unknown direction '{direction}'. Use Forward or Backward.", nameof(direction));
    }

    private static SemanticPointer DeserializePointer(string pointerJson)
    {
        try
        {
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };

            return JsonSerializer.Deserialize<LinearPointer>(pointerJson, options)
                   ?? JsonSerializer.Deserialize<SemanticPointer>(pointerJson, options)
                   ?? throw new ArgumentException("Pointer payload is empty.", nameof(pointerJson));
        }
        catch (Exception ex)
        {
            throw new ArgumentException($"Pointer cannot be parsed: {ex.Message}", nameof(pointerJson));
        }
    }
}
