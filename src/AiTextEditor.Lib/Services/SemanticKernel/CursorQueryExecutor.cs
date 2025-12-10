using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AiTextEditor.Lib.Services;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace AiTextEditor.Lib.Services.SemanticKernel;

public sealed class CursorQueryExecutor
{
    private readonly DocumentContext documentContext;
    private readonly IChatCompletionService chatCompletionService;
    private readonly ILogger<CursorQueryExecutor> logger;

    public CursorQueryExecutor(DocumentContext documentContext, IChatCompletionService chatCompletionService, ILogger<CursorQueryExecutor> logger)
    {
        this.documentContext = documentContext ?? throw new ArgumentNullException(nameof(documentContext));
        this.chatCompletionService = chatCompletionService ?? throw new ArgumentNullException(nameof(chatCompletionService));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<CursorQueryResult> ExecuteQueryOverCursorAsync(string cursorName, string query, ChatHistory? history = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cursorName);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        history ??= CreateChatHistory(query);

        var portion = documentContext.CursorContext.GetNextPortion(cursorName);
        while (portion != null)
        {
            var prompt = BuildPortionPrompt(portion, query);
            history.AddUserMessage(prompt);

            var response = await chatCompletionService.GetChatMessageContentsAsync(history, CreateSettings(), cancellationToken: cancellationToken);
            var content = response.FirstOrDefault()?.Content ?? string.Empty;
            logger.LogInformation("Cursor {CursorName} iteration yielded response: {Response}", cursorName, content);

            var decision = ParseDecision(content);
            if (decision.Status == CursorDecisionStatus.Found || decision.Status == CursorDecisionStatus.Complete)
            {
                return new CursorQueryResult(true, decision.Result);
            }

            if (!portion.HasMore)
            {
                break;
            }

            portion = documentContext.CursorContext.GetNextPortion(cursorName);
        }

        return new CursorQueryResult(false, null);
    }

    public async Task<CursorMapResult> ExecutePortionTasksAsync(string cursorName, string instruction, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cursorName);
        ArgumentException.ThrowIfNullOrWhiteSpace(instruction);

        var results = new List<PortionTaskResult>();
        var portionIndex = 0;
        var portion = documentContext.CursorContext.GetNextPortion(cursorName);

        if (portion == null)
        {
            return new CursorMapResult(false, results);
        }

        while (portion != null)
        {
            var history = CreatePortionTaskHistory(portion, instruction, portionIndex);
            var response = await chatCompletionService.GetChatMessageContentsAsync(history, CreateSettings(), cancellationToken: cancellationToken);
            var content = response.FirstOrDefault()?.Content ?? string.Empty;
            logger.LogInformation("Map over cursor {CursorName} portion {PortionIndex} yielded response: {Response}", cursorName, portionIndex, content);

            var portionResult = ParsePortionResult(content, portionIndex);
            results.Add(portionResult);

            if (!portion.HasMore)
            {
                break;
            }

            portion = documentContext.CursorContext.GetNextPortion(cursorName);
            portionIndex++;
        }

        return new CursorMapResult(true, results);
    }

    private static ChatHistory CreateChatHistory(string query)
    {
        var history = new ChatHistory();
        var builder = new StringBuilder();
        builder.AppendLine("You receive sequential portions of a linear document cursor.");
        builder.AppendLine("Analyze each portion and respond with JSON using one of the statuses: 'continue', 'found', or 'complete'.");
        builder.AppendLine("Use 'found' when the portion satisfies the task. Use 'complete' when you are confident the task is fully solved.");
        builder.AppendLine("Always respond with compact JSON: {\"status\":\"found|continue|complete\",\"result\":\"<details>\"}.");
        builder.Append("Task: ").Append(query);

        history.AddSystemMessage(builder.ToString());
        return history;
    }

    private static ChatHistory CreatePortionTaskHistory(CursorPortion portion, string instruction, int portionIndex)
    {
        var history = new ChatHistory();
        var builder = new StringBuilder();
        builder.AppendLine("Process the given cursor portion independently.");
        builder.AppendLine("Return JSON only: {\"portionIndex\":<number>,\"result\":\"short answer\"}.");
        builder.AppendLine("Do not rely on other portions or global context.");
        builder.AppendLine($"Portion index: {portionIndex}");
        builder.AppendLine($"Task: {instruction}");
        builder.AppendLine("Items:");

        foreach (var item in portion.Items)
        {
            builder.AppendLine($"- Pointer={item.Pointer.Serialize()}; Type={item.Type}; Level={(item.Level.HasValue ? item.Level.Value.ToString() : "-")}; Text={item.Text}");
        }

        builder.AppendLine("Return JSON only.");
        history.AddSystemMessage(builder.ToString());
        return history;
    }

    private static string BuildPortionPrompt(CursorPortion portion, string query)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Cursor: {portion.CursorName}");
        builder.AppendLine($"Has more: {portion.HasMore}");
        builder.AppendLine($"Task: {query}");
        builder.AppendLine("Items:");

        foreach (var item in portion.Items)
        {
            builder.AppendLine($"- Pointer={item.Pointer.Serialize()}; Type={item.Type}; Level={(item.Level.HasValue ? item.Level.Value.ToString() : "-")}; Text={item.Text}");
        }

        builder.AppendLine("Return JSON only.");
        return builder.ToString();
    }

    private static CursorDecision ParseDecision(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            var statusText = root.GetProperty("status").GetString();
            var result = root.TryGetProperty("result", out var resultElement) && resultElement.ValueKind != JsonValueKind.Null
                ? resultElement.GetString()
                : null;

            return statusText?.ToLowerInvariant() switch
            {
                "found" => new CursorDecision(CursorDecisionStatus.Found, result),
                "complete" => new CursorDecision(CursorDecisionStatus.Complete, result),
                _ => new CursorDecision(CursorDecisionStatus.Continue, result)
            };
        }
        catch (Exception)
        {
            return new CursorDecision(CursorDecisionStatus.Continue, null);
        }
    }

    private static PortionTaskResult ParsePortionResult(string content, int portionIndex)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            var result = root.TryGetProperty("result", out var resultElement) && resultElement.ValueKind != JsonValueKind.Null
                ? resultElement.GetString()
                : null;

            return new PortionTaskResult(portionIndex, result);
        }
        catch (Exception)
        {
            return new PortionTaskResult(portionIndex, null);
        }
    }

    private static OpenAIPromptExecutionSettings CreateSettings()
    {
        return new OpenAIPromptExecutionSettings
        {
            Temperature = 0,
            TopP = 0
        };
    }

    private enum CursorDecisionStatus
    {
        Continue,
        Found,
        Complete
    }

    private sealed record CursorDecision(CursorDecisionStatus Status, string? Result);
}

public sealed record CursorMapResult(bool Success, IReadOnlyList<PortionTaskResult> Portions);
