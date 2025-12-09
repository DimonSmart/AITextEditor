using System;
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

    private static string BuildPortionPrompt(CursorPortion portion, string query)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Cursor: {portion.CursorName}");
        builder.AppendLine($"Has more: {portion.HasMore}");
        builder.AppendLine($"Task: {query}");
        builder.AppendLine("Items:");

        foreach (var item in portion.Items)
        {
            builder.AppendLine($"- Pointer={item.Pointer.SemanticNumber}; Type={item.Type}; Level={(item.Level.HasValue ? item.Level.Value.ToString() : "-")}; Text={item.Text}");
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
