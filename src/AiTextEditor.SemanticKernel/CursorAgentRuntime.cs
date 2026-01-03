using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AiTextEditor.Lib.Model;
using AiTextEditor.Lib.Services;
using AiTextEditor.Lib.Services.SemanticKernel;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AiTextEditor.SemanticKernel;

public sealed class CursorAgentRuntime : ICursorAgentRuntime
{
    private readonly ICursorStore cursorStore;
    private readonly IChatCompletionService chatService;
    private readonly ICursorAgentPromptBuilder promptBuilder;
    private readonly ICursorAgentResponseParser responseParser;
    private readonly ICursorEvidenceCollector evidenceCollector;
    private readonly CursorAgentLimits limits;
    private readonly ILogger<CursorAgentRuntime> logger;

    public CursorAgentRuntime(
        ICursorStore cursorStore,
        IChatCompletionService chatService,
        ICursorAgentPromptBuilder promptBuilder,
        ICursorAgentResponseParser responseParser,
        ICursorEvidenceCollector evidenceCollector,
        CursorAgentLimits limits,
        ILogger<CursorAgentRuntime> logger)
    {
        this.cursorStore = cursorStore ?? throw new ArgumentNullException(nameof(cursorStore));
        this.chatService = chatService ?? throw new ArgumentNullException(nameof(chatService));
        this.promptBuilder = promptBuilder ?? throw new ArgumentNullException(nameof(promptBuilder));
        this.responseParser = responseParser ?? throw new ArgumentNullException(nameof(responseParser));
        this.evidenceCollector = evidenceCollector ?? throw new ArgumentNullException(nameof(evidenceCollector));
        this.limits = limits ?? throw new ArgumentNullException(nameof(limits));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<CursorAgentStepResult> RunStepAsync(
        CursorAgentRequest request,
        CursorPortionView portion,
        CursorAgentState state,
        int step,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(portion);
        ArgumentNullException.ThrowIfNull(state);

        var agentSystemPrompt = promptBuilder.BuildAgentSystemPrompt();
        var taskDefinitionPrompt = promptBuilder.BuildTaskDefinitionPrompt(request);

        var evidenceSnapshot = promptBuilder.BuildEvidenceSnapshot(state);
        var batchMessage = promptBuilder.BuildBatchMessage(portion, step);

        var command = await GetNextCommandAsync(agentSystemPrompt, taskDefinitionPrompt, evidenceSnapshot, batchMessage, cancellationToken, step);

        if (command == null)
        {
            logger.LogError("Agent response malformed.");
            throw new InvalidOperationException("Agent response malformed.");
        }

        return new CursorAgentStepResult(command.Action, command.BatchFound, command.NewEvidence, command.Progress, command.NeedMoreContext, portion.HasMore);
    }

    public async Task<CursorAgentResult> RunAsync(string cursorName, CursorAgentRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cursorName);
        ArgumentNullException.ThrowIfNull(request);

        if (!cursorStore.TryGetCursor(cursorName, out var cursor) || cursor == null)
        {
            throw new InvalidOperationException($"cursor_not_found: {cursorName}");
        }

        var maxSteps = Math.Clamp(limits.DefaultMaxSteps, 1, limits.MaxStepsLimit);
        var agentSystemPrompt = promptBuilder.BuildAgentSystemPrompt();
        var taskDefinitionPrompt = promptBuilder.BuildTaskDefinitionPrompt(request);

        var cursorAgentState = new CursorAgentState(Array.Empty<EvidenceItem>());

        var afterPointer = request.StartAfterPointer;
        string? summary = null;
        string? stopReason = null;
        var stepsUsed = 0;

        for (var step = 0; step < maxSteps; step++)
        {
            var portion = cursor.NextPortion();
            if (!portion.Items.Any())
            {
                stopReason = "cursor_complete";
                break;
            }

            var cursorPortionView = CursorPortionView.FromPortion(portion);
            afterPointer = cursorPortionView.Items[^1].SemanticPointer;
            logger.LogDebug(
                "{Event}: cursor={Cursor}, count={Count}, hasMore={HasMore}",
                cursorPortionView.HasMore ? "cursor_batch" : "cursor_batch_complete",
                cursorName,
                cursorPortionView.Items.Count,
                cursorPortionView.HasMore);

            var evidenceSnapshot = promptBuilder.BuildEvidenceSnapshot(cursorAgentState);
            var batchMessage = promptBuilder.BuildBatchMessage(cursorPortionView, step);

            var command = await GetNextCommandAsync(agentSystemPrompt, taskDefinitionPrompt, evidenceSnapshot, batchMessage, cancellationToken, step);
            stepsUsed = step + 1;

            if (command == null)
            {
                logger.LogError("Agent response malformed.");
                throw new InvalidOperationException("Agent response malformed.");
            }

            var updatedSummary = string.IsNullOrWhiteSpace(command.Progress) ? summary : Truncate(command.Progress, limits.MaxSummaryLength);
            var maxEvidenceCount = request.MaxEvidenceCount ?? limits.DefaultMaxFound;
            cursorAgentState = evidenceCollector.AppendEvidence(cursorAgentState, cursorPortionView, command.NewEvidence ?? Array.Empty<EvidenceItem>(), maxEvidenceCount);
            summary = updatedSummary ?? summary;

            if (ShouldStop(command.Action, cursorPortionView.HasMore, stepsUsed, maxSteps, out stopReason))
            {
                break;
            }
        }

        stopReason ??= "max_steps";
        var cursorComplete = cursor.IsComplete || string.Equals(stopReason, "cursor_complete", StringComparison.OrdinalIgnoreCase);
        return await BuildResultByFinalizerAsync(request.TaskDescription, cursorAgentState, summary, stopReason, afterPointer, cursorComplete, stepsUsed, cancellationToken);
    }

    private async Task<CursorAgentResult> BuildResultByFinalizerAsync(
        string taskDescription,
        CursorAgentState state,
        string? summary,
        string stopReason,
        string? nextAfterPointer,
        bool cursorComplete,
        int stepsUsed,
        CancellationToken cancellationToken)
    {
        if (state.Evidence.Count == 0)
        {
            return new CursorAgentResult(
                false,
                summary ?? stopReason,
                null,
                null,
                null,
                state.Evidence,
                nextAfterPointer,
                cursorComplete);
        }

        var history = new ChatHistory();
        history.AddSystemMessage(promptBuilder.BuildFinalizerSystemPrompt());

        var evidenceJson = evidenceCollector.SerializeEvidence(state.Evidence);
        history.AddUserMessage(promptBuilder.BuildFinalizerUserMessage(taskDescription, evidenceJson, cursorComplete, stepsUsed, nextAfterPointer));

        var response = await chatService.GetChatMessageContentsAsync(history, promptBuilder.CreateSettings(), cancellationToken: cancellationToken);
        var message = response.FirstOrDefault();
        var content = message?.Content ?? string.Empty;
        LogCompletionSkeleton(stepsUsed, message);
        LogRawCompletion(stepsUsed, content);

        var parsed = responseParser.ParseFinalizer(content);
        if (parsed == null || parsed.Decision == "not_found")
        {
            return new CursorAgentResult(
               false,
               summary ?? stopReason,
               null,
               null,
               null,
               state.Evidence,
               nextAfterPointer,
               cursorComplete);
        }

        var normalizedPointerFrom = NormalizePointer(parsed.SemanticPointerFrom);
        if (parsed.Decision != "success" || string.IsNullOrWhiteSpace(normalizedPointerFrom) || !state.Evidence.Any(e => e.Pointer.Equals(normalizedPointerFrom, StringComparison.OrdinalIgnoreCase)))
        {
            logger.LogWarning("finalizer_pointer_missing_or_invalid");
            return new CursorAgentResult(
               false,
               "finalizer_missing_pointer",
               null,
               null,
               null,
               state.Evidence,
               nextAfterPointer,
               cursorComplete);
        }

        var finalSummary = string.IsNullOrWhiteSpace(parsed.Summary) ? summary : Truncate(parsed.Summary, limits.MaxSummaryLength);

        return new CursorAgentResult(
            true,
            finalSummary,
            normalizedPointerFrom,
            Truncate(parsed.Excerpt, limits.MaxExcerptLength),
            parsed.WhyThis,
            state.Evidence,
            nextAfterPointer,
            cursorComplete);
    }

    private async Task<AgentCommand?> GetNextCommandAsync(
        string agentSystemPrompt,
        string taskDefinitionPrompt,
        string evidenceSnapshot,
        string batchMessage,
        CancellationToken cancellationToken,
        int step)
    {
        var history = new ChatHistory();
        history.AddSystemMessage(agentSystemPrompt);
        history.AddUserMessage(taskDefinitionPrompt);
        history.AddUserMessage(evidenceSnapshot);
        history.AddUserMessage(batchMessage);

        var response = await chatService.GetChatMessageContentsAsync(history, promptBuilder.CreateSettings(), cancellationToken: cancellationToken);
        var message = response.FirstOrDefault();
        var content = message?.Content ?? string.Empty;
        LogCompletionSkeleton(step, message);
        LogRawCompletion(step, content);

        var parsed = responseParser.ParseCommand(content, out var parsedFragment, out var multipleActions, out var finishDetected);
        if (multipleActions)
        {
            logger.LogWarning("multiple actions returned");
        }

        if (parsed != null)
        {
            logger.LogDebug(
                "cursor_agent_parsed: step={Step}, action={Action}, batchFound={BatchFound}, finishFound={Finish}, parsedAction={ParsedAction}",
                step,
                parsed.Action,
                parsed.BatchFound,
                finishDetected,
                Truncate(parsedFragment ?? string.Empty, 500));
        }

        return parsed?.WithRawContent(parsedFragment ?? content);
    }

    private static bool ShouldStop(string actionRaw, bool cursorHasMore, int step, int maxSteps, out string reason)
    {
        var action = NormalizeAction(actionRaw);

        if (action == "stop")
        {
            reason = "agent_stopped";
            return true;
        }

        if (!cursorHasMore)
        {
            reason = "cursor_complete";
            return true;
        }

        if (step >= maxSteps)
        {
            reason = "max_steps";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    private static string NormalizeAction(string? action) => action?.Trim().ToLowerInvariant() switch
    {
        "stop" => "stop",
        "continue" => "continue",
        _ => "continue"
    };

    private static string? NormalizePointer(string? pointer)
    {
        if (string.IsNullOrWhiteSpace(pointer))
        {
            return null;
        }

        return SemanticPointer.TryParse(pointer, out var parsed) ? parsed!.ToCompactString() : null;
    }

    private void LogCompletionSkeleton(int step, object? message)
    {
        if (message == null)
        {
            logger.LogInformation("cursor_agent_call: step={Step}, model=<unknown>, tokens=<unknown>, result=<empty>", step);
            return;
        }

        var metadata = message.GetType().GetProperty("Metadata")?.GetValue(message) as IReadOnlyDictionary<string, object?>;
        var modelId = message.GetType().GetProperty("ModelId")?.GetValue(message) ?? "<unknown>";

        var tokens = metadata?.TryGetValue("usage", out var usage) == true ? usage : "<unknown>";

        logger.LogInformation("cursor_agent_call: step={Step}, model={Model}, tokens={Tokens}, result=<received>", step, modelId, tokens);
    }

    private void LogRawCompletion(int step, string content)
    {
        var snippet = Truncate(content, 1000);
        logger.LogDebug("cursor_agent_raw: step={Step}, snippet={Snippet}", step, snippet);
        logger.LogDebug("cursor_agent_raw_len: step={Step}, len={Len}", step, content.Length);
        logger.LogDebug("cursor_agent_raw_head: step={Step}, head={Head}", step, content[..Math.Min(300, content.Length)]);
        logger.LogDebug("cursor_agent_raw_tail: step={Step}, tail={Tail}", step, content[^Math.Min(300, content.Length)..]);
    }

    private string? Truncate(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
        {
            return text;
        }

        return text[..maxLength] + $"... (+{text.Length - maxLength} chars)";
    }
}
