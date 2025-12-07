using AiTextEditor.Lib.Interfaces;
using AiTextEditor.Lib.Model;
using Xunit.Abstractions;

namespace AiTextEditor.Tests.Infrastructure;

public class FakeLlmEditor : ILlmEditor
{
    private readonly ITestOutputHelper _output;

    public FakeLlmEditor(ITestOutputHelper output)
    {
        _output = output;
    }

    public Task<List<EditOperation>> GetEditOperationsAsync(
        string targetSetId,
        List<LinearItem> contextItems,
        string rawUserText,
        string instruction,
        CancellationToken ct = default)
    {
        _output.WriteLine("FakeLlmEditor called.");
        _output.WriteLine($"User Request: {rawUserText}");
        _output.WriteLine($"Instruction: {instruction}");
        _output.WriteLine($"TargetSet: {targetSetId}");
        _output.WriteLine($"Context Items Count: {contextItems.Count}");
        foreach (var item in contextItems.Take(5))
        {
            _output.WriteLine($" - Item {item.Index} ({item.Type} {item.Pointer.SemanticNumber}): {item.Text.Substring(0, Math.Min(50, item.Text.Length))}...");
        }

        // Return a dummy operation to satisfy the planner
        var ops = new List<EditOperation>
        {
            new EditOperation
            {
                Action = EditActionType.Keep,
                TargetBlockId = targetSetId,
                NewBlock = null
            }
        };

        return Task.FromResult(ops);
    }
}
