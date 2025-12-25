/*
using System.Text;
using System.Text.Json;
using System.ComponentModel;
using AiTextEditor.Lib.Model;
using AiTextEditor.Lib.Services.SemanticKernel;
using Microsoft.SemanticKernel;
using Microsoft.Extensions.Logging;
using AiTextEditor.Lib.Services;

namespace AiTextEditor.SemanticKernel;

public sealed class AgentPlugin(
    CursorRegistry registry,
    ICursorAgentRuntime cursorAgentRuntime,
    CursorAgentLimits limits,
    ILogger<AgentPlugin> logger)
{
    [KernelFunction("run_agent")]
    [Description("Runs the legacy cursor agent for a given cursor name.")]
    public async Task<string> RunAgent(
        [Description("Name of the cursor")] string cursorName,
        [Description("Task description for the agent")] string taskDescription,
        [Description("Optional: Stop scanning after finding this many evidence items.")] int maxEvidenceCount = -1)
    {
        if (!registry.TryGetCursor(cursorName, out var cursor) || cursor == null)
        {
            throw new InvalidOperationException($"Cursor '{cursorName}' not found.");
        }

        if (!registry.TryGetContext(cursorName, out var searchContext) || searchContext == null)
        {
            throw new InvalidOperationException($"Context for cursor '{cursorName}' not found.");
        }

        int? actualMaxEvidenceCount = maxEvidenceCount > 0 ? maxEvidenceCount : null;

        CursorAgentStepResult? lastResult = null;
        const int MaxAutoAdvance = 10; // Allow scanning up to ~120 items in one go
        var aggregatedEvidence = new List<EvidenceItem>();

        for (int i = 0; i < MaxAutoAdvance; i++)
        {
            var portion = cursor.NextPortion();
            
            if (!portion.Items.Any())
            {
                if (lastResult == null)
                {
                    return JsonSerializer.Serialize(new { decision = "cursor_complete" });
                }
                else
                {
                    lastResult = new CursorAgentStepResult(
                        lastResult.Action,
                        lastResult.BatchFound,
                        aggregatedEvidence,
                        lastResult.Progress,
                        lastResult.NeedMoreContext,
                        false // HasMore
                    );
                    break;
                }
            }

            var cursorPortionView = CursorPortionView.FromPortion(portion);
            var state = registry.GetState(cursorName);
            var step = registry.GetStep(cursorName);

            var request = new CursorAgentRequest(taskDescription, Context: searchContext, MaxEvidenceCount: actualMaxEvidenceCount);
            var result = await cursorAgentRuntime.RunStepAsync(request, cursorPortionView, state, step);

            if (result.NewEvidence != null)
            {
                aggregatedEvidence.AddRange(result.NewEvidence);
            }

            registry.UpdateState(cursorName, state.WithEvidence(result.NewEvidence ?? Array.Empty<EvidenceItem>(), limits.DefaultMaxFound));
            registry.IncrementStep(cursorName);
            lastResult = result;

            if (actualMaxEvidenceCount.HasValue && aggregatedEvidence.Count >= actualMaxEvidenceCount.Value)
            {
                lastResult = new CursorAgentStepResult("stop", true, result.NewEvidence, result.Progress, result.NeedMoreContext, result.HasMore);
                break;
            }

            bool shouldContinue = 
                string.Equals(result.Action, "continue", StringComparison.OrdinalIgnoreCase) &&
                result.HasMore;

            if (!shouldContinue)
            {
                break;
            }
            
            logger.LogInformation("RunAgent: Auto-advancing cursor '{CursorName}' (step {Step})...", cursorName, i + 1);
        }

        if (lastResult != null)
        {
            lastResult = new CursorAgentStepResult(
                lastResult.Action,
                lastResult.BatchFound,
                aggregatedEvidence,
                lastResult.Progress,
                lastResult.NeedMoreContext,
                lastResult.HasMore
            );
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Action: {lastResult?.Action ?? "unknown"}");
        
        if (aggregatedEvidence.Any())
        {
            sb.AppendLine("Evidence:");
            foreach (var item in aggregatedEvidence)
            {
                sb.AppendLine($"- Pointer: {item.Pointer}");
                sb.AppendLine($"  Excerpt: {item.Excerpt.Replace("\n", " ").Replace("\r", "")}");
                sb.AppendLine($"  Reason: {item.Reason}");
                sb.AppendLine();
            }
        }
        else
        {
            sb.AppendLine("No new evidence found in this batch.");
        }

        if (lastResult?.HasMore == false)
        {
            sb.AppendLine("Status: Cursor complete (no more items).");
        }
        else
        {
            sb.AppendLine("Status: More items available.");
        }

        return sb.ToString();
    }
}
*/
