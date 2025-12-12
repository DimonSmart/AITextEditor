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
    private const int MaxCursorNameLength = 96;
    private static readonly string[] AllowedModeNames = Enum.GetNames<CursorAgentMode>();

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
        ValidateCursorName(cursorName);
        ValidatePortionLimits(maxElements, maxBytes);

        var parameters = new CursorParameters(maxElements, maxBytes, includeContent);
        documentContext.CursorContext.CreateCursor(cursorName, parameters, forward);
        var handle = new CursorHandle(cursorName, forward, parameters.MaxElements, parameters.MaxBytes, parameters.IncludeContent);
        logger.LogInformation("cursor_create: {CursorName}, forward={Forward}", cursorName, forward);
        return JsonSerializer.Serialize(handle, SerializerOptions);
    }

    [KernelFunction]
    [Description("Return the next chunk from a cursor.")]
    public string CursorNext([Description("Cursor name returned by CreateCursor.")] string cursorName)
    {
        ValidateCursorName(cursorName);

        var portion = documentContext.CursorContext.GetNextPortion(cursorName);
        if (portion == null)
        {
            var cursorExists = documentContext.CursorContext.Cursors.ContainsKey(cursorName);
            var message = cursorExists
                ? $"Cursor '{cursorName}' has reached the end. Reset it before requesting more portions."
                : $"Cursor '{cursorName}' does not exist. Call CreateCursor before CursorNext.";

            throw new InvalidOperationException(message);
        }

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
        ValidateCursorName(cursorName);

        if (!Enum.TryParse<CursorAgentMode>(mode, true, out var parsedMode))
        {
            var allowedValues = string.Join(", ", AllowedModeNames);
            var error = CreateErrorResult($"Unsupported cursor agent mode '{mode}'. Allowed values: {allowedValues}.", targetSetId);
            logger.LogWarning("run_cursor_agent_invalid_mode: mode={Mode}", mode);
            return JsonSerializer.Serialize(error, SerializerOptions);
        }

        if (parsedMode == CursorAgentMode.CollectToTargetSet && string.IsNullOrWhiteSpace(targetSetId))
        {
            var error = CreateErrorResult("targetSetId is required for CollectToTargetSet mode.");
            logger.LogWarning("run_cursor_agent_missing_target_set");
            return JsonSerializer.Serialize(error, SerializerOptions);
        }

        if (!TryResolveMaxSteps(maxSteps, out var resolvedSteps, out var stepsError))
        {
            var error = CreateErrorResult(stepsError!, targetSetId);
            logger.LogWarning("run_cursor_agent_invalid_steps: {Error}", stepsError);
            return JsonSerializer.Serialize(error, SerializerOptions);
        }

        var request = new CursorAgentRequest(cursorName, parsedMode, taskDescription, targetSetId, resolvedSteps);
        var result = await cursorAgentRuntime.RunAsync(request);

        logger.LogInformation("run_cursor_agent: cursor={Cursor}, mode={Mode}, success={Success}", cursorName, parsedMode, result.Success);
        return JsonSerializer.Serialize(result, SerializerOptions);
    }

    private static void ValidateCursorName(string cursorName)
    {
        if (string.IsNullOrWhiteSpace(cursorName))
        {
            throw new ArgumentException("cursorName must not be empty.", nameof(cursorName));
        }

        if (cursorName.Length > MaxCursorNameLength)
        {
            throw new ArgumentException($"cursorName cannot exceed {MaxCursorNameLength} characters.", nameof(cursorName));
        }
    }

    private static void ValidatePortionLimits(int maxElements, int maxBytes)
    {
        if (maxElements <= 0 || maxElements > CursorParameters.MaxElementsUpperBound)
        {
            throw new ArgumentOutOfRangeException(nameof(maxElements),
                $"maxElements must be between 1 and {CursorParameters.MaxElementsUpperBound}.");
        }

        if (maxBytes <= 0 || maxBytes > CursorParameters.MaxBytesUpperBound)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes),
                $"maxBytes must be between 1 and {CursorParameters.MaxBytesUpperBound}.");
        }
    }

    private static CursorAgentResult CreateErrorResult(string reason, string? targetSetId = null)
        => new(false, reason, null, null, targetSetId);

    private static bool TryResolveMaxSteps(int? requestedSteps, out int resolvedSteps, out string? error)
    {
        resolvedSteps = CursorAgentRuntime.DefaultMaxSteps;
        error = null;

        if (!requestedSteps.HasValue)
        {
            return true;
        }

        if (requestedSteps.Value <= 0)
        {
            error = $"maxSteps must be between 1 and {CursorAgentRuntime.MaxStepsLimit}.";
            return false;
        }

        if (requestedSteps.Value > CursorAgentRuntime.MaxStepsLimit)
        {
            error = $"maxSteps must not exceed {CursorAgentRuntime.MaxStepsLimit}.";
            return false;
        }

        resolvedSteps = requestedSteps.Value;
        return true;
    }
}
