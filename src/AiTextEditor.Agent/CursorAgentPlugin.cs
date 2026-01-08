using System.ComponentModel;
using AiTextEditor.Core.Model;
using AiTextEditor.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace AiTextEditor.Agent;

public sealed class CursorAgentPlugin(
    ICursorAgentRuntime runtime,
    ILogger<CursorAgentPlugin> logger)
{
    private readonly ICursorAgentRuntime runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    private readonly ILogger<CursorAgentPlugin> logger = logger ?? throw new ArgumentNullException(nameof(logger));

    [KernelFunction("run_cursor_agent")]
    [Description("Runs the cursor agent over an existing cursor and returns a result summary.")]
    public async Task<CursorAgentResult> RunCursorAgent(
        [Description("Cursor name created earlier.")] string cursorName,
        [Description("Task description for the agent.")] string taskDescription,
        [Description("Pointer to start after (optional).")] string startAfterPointer = "",
        [Description("Optional extra context for the agent.")] string? context = null,
        [Description("Optional max evidence count.")] int? maxEvidenceCount = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cursorName);
        ArgumentException.ThrowIfNullOrWhiteSpace(taskDescription);

        var normalizedStartAfter = string.IsNullOrWhiteSpace(startAfterPointer) ? null : startAfterPointer;
        logger.LogInformation("RunCursorAgent: cursor={Cursor}, task={Task}, startAfter={StartAfter}", cursorName, taskDescription, normalizedStartAfter ?? "<none>");

        var request = new CursorAgentRequest(taskDescription, normalizedStartAfter, context, maxEvidenceCount);
        return await runtime.RunAsync(cursorName, request, cancellationToken);
    }
}
