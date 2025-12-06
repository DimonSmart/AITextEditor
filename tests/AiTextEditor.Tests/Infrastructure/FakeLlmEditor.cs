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
        List<Block> contextBlocks,
        string rawUserText,
        string instruction,
        CancellationToken ct = default)
    {
        _output.WriteLine("FakeLlmEditor called.");
        _output.WriteLine($"User Request: {rawUserText}");
        _output.WriteLine($"Instruction: {instruction}");
        _output.WriteLine($"Context Blocks Count: {contextBlocks.Count}");
        foreach (var block in contextBlocks.Take(5))
        {
            _output.WriteLine($" - Block {block.Id} ({block.Type}): {block.PlainText.Substring(0, Math.Min(50, block.PlainText.Length))}...");
        }

        // Return a dummy operation to satisfy the planner
        var ops = new List<EditOperation>
        {
            new EditOperation
            {
                Action = EditActionType.Keep,
                TargetBlockId = contextBlocks.FirstOrDefault()?.Id,
                NewBlock = null
            }
        };

        return Task.FromResult(ops);
    }
}
