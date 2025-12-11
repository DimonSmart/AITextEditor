using System.ComponentModel;
using System.Text.Json;
using AiTextEditor.Lib.Model;
using AiTextEditor.Lib.Services;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace AiTextEditor.Lib.Services.SemanticKernel;

public class CursorAgentPlugin(
    DocumentContext documentContext,
    CursorAgentRuntime cursorAgentRuntime,
    ILogger<CursorAgentPlugin> logger)
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly DocumentContext documentContext = documentContext;
    private readonly CursorAgentRuntime cursorAgentRuntime = cursorAgentRuntime;
    private readonly ILogger<CursorAgentPlugin> logger = logger;

    [KernelFunction]
    [Description("Create or reset a cursor with paging settings.")]
    public string CreateCursor(
        [Description("Custom cursor name.")] string cursorName,
        [Description("Walk forward when true, backward when false.")] bool forward,
        [Description("Maximum items per portion.")] int maxElements,
        [Description("Maximum bytes per portion.")] int maxBytes,
        [Description("Include markdown content in responses.")] bool includeContent)
    {
        var parameters = new CursorParameters(maxElements, maxBytes, includeContent);
        documentContext.CursorContext.CreateCursor(cursorName, parameters, forward);
        var handle = new CursorHandle(cursorName, forward, parameters.MaxElements, parameters.MaxBytes, parameters.IncludeContent);
        logger.LogInformation("cursor_create: {CursorName}, forward={Forward}", cursorName, forward);
        return JsonSerializer.Serialize(handle, SerializerOptions);
    }

    [KernelFunction]
    [Description("Return the next chunk from a cursor.")]
    public string CursorNext([Description("Cursor name from cursor_create.")] string cursorName)
    {
        var portion = documentContext.CursorContext.GetNextPortion(cursorName)
            ?? throw new InvalidOperationException($"Cursor '{cursorName}' is not defined.");

        var snapshot = CursorPortionView.FromPortion(portion);
        logger.LogInformation("cursor_next: {CursorName}, count={Count}, hasMore={HasMore}", cursorName, snapshot.Items.Count, snapshot.HasMore);
        return JsonSerializer.Serialize(snapshot, SerializerOptions);
    }

    [KernelFunction]
    [Description("Create a target set for collecting item indices.")]
    public string TargetSetCreate([Description("Optional human-readable label.")] string? name = null)
    {
        var id = documentContext.TargetSetContext.Create(name);
        logger.LogInformation("target_set_create: {TargetSetId}", id);
        return JsonSerializer.Serialize(new { targetSetId = id }, SerializerOptions);
    }

    [KernelFunction]
    [Description("Add item indices to a target set.")]
    public string TargetSetAdd(
        [Description("Target set identifier.")] string targetSetId,
        [Description("Indices to add.")] int[] itemIndices)
    {
        var added = documentContext.TargetSetContext.Add(targetSetId, itemIndices);
        logger.LogInformation("target_set_add: {TargetSetId}, count={Count}, success={Success}", targetSetId, itemIndices.Length, added);
        return JsonSerializer.Serialize(new { targetSetId, success = added, count = itemIndices.Length }, SerializerOptions);
    }

    [KernelFunction]
    [Description("Get all indices from a target set.")]
    public string TargetSetGet([Description("Target set identifier.")] string targetSetId)
    {
        var indices = documentContext.TargetSetContext.Get(targetSetId)
                      ?? throw new InvalidOperationException($"Target set '{targetSetId}' not found.");

        logger.LogInformation("target_set_get: {TargetSetId}, count={Count}", targetSetId, indices.Count);
        return JsonSerializer.Serialize(new { targetSetId, itemIndices = indices }, SerializerOptions);
    }

    [KernelFunction]
    [Description("Launch a cursor agent with a concise system prompt.")]
    public async Task<string> RunCursorAgent(
        [Description("Cursor name to operate on.")] string cursorName,
        [Description("Mode: FirstMatch, CollectToTargetSet, or AggregateSummary.")] string mode,
        [Description("Natural language task for the agent.")] string taskDescription,
        [Description("Target set id for CollectToTargetSet mode.")] string? targetSetId = null,
        [Description("Optional safety limit for steps.")] int? maxSteps = null)
    {
        var parsedMode = Enum.Parse<CursorAgentMode>(mode, true);
        var request = new CursorAgentRequest(cursorName, parsedMode, taskDescription, targetSetId, maxSteps);
        var result = await cursorAgentRuntime.RunAsync(request);

        logger.LogInformation("run_cursor_agent: cursor={Cursor}, mode={Mode}, success={Success}", cursorName, parsedMode, result.Success);
        return JsonSerializer.Serialize(result, SerializerOptions);
    }
}
