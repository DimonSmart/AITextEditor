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
        [Description("Walk forward when true, backward when false.")] bool forward,
        [Description("Maximum items per portion.")] int maxElements,
        [Description("Maximum bytes per portion.")] int maxBytes,
        [Description("Include markdown content in responses.")] bool includeContent,
        [Description("Mode: FirstMatch, CollectToTargetSet, or AggregateSummary.")] string mode,
        [Description("Natural language task for the agent.")] string taskDescription,
        [Description("Target set id for CollectToTargetSet mode.")] string? targetSetId = null,
        [Description("Optional safety limit for steps.")] int? maxSteps = null,
        [Description("Existing task id to continue the same agent session.")] string? taskId = null,
        [Description("Serialized TaskState to resume from a previous step.")] TaskState? state = null)
    {
        maxElements = Math.Clamp(maxElements, 1, CursorParameters.MaxElementsUpperBound);
        maxBytes = Math.Clamp(maxBytes, 1, CursorParameters.MaxBytesUpperBound);

        ValidatePortionLimits(maxElements, maxBytes);
        var parameters = new CursorParameters(maxElements, maxBytes, includeContent);

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

        var request = new CursorAgentRequest(parameters, forward, parsedMode, taskDescription, targetSetId, resolvedSteps, taskId, state);
        var result = await cursorAgentRuntime.RunAsync(request);

        logger.LogInformation("run_cursor_agent: direction={Direction}, mode={Mode}, success={Success}, maxElements={MaxElements}, maxBytes={MaxBytes}, includeContent={IncludeContent}",
            forward ? "forward" : "backward", parsedMode, result.Success, parameters.MaxElements, parameters.MaxBytes, parameters.IncludeContent);
        
        // Create a lightweight result for the LLM to avoid token limit issues and distractions
        var lightweightResult = new
        {
            result.Success,
            result.FirstItemIndex,
            result.Summary,
            result.TaskId,
            State = new 
            { 
                result.State?.Goal, 
                result.State?.Found, 
                result.State?.Progress,
                result.State?.Limits,
                result.State?.Evidence 
            },
            result.PointerFrom,
            result.PointerTo,
            result.Excerpt,
            result.WhyThis,
            result.Evidence
        };

        return JsonSerializer.Serialize(lightweightResult, SerializerOptions);
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
