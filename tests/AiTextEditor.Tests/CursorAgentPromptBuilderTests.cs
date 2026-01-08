using System.Text.Json;
using AiTextEditor.Core.Model;
using AiTextEditor.Core.Services;
using AiTextEditor.Agent;
using Xunit;

namespace AiTextEditor.Tests;

public class CursorAgentPromptBuilderTests
{
    private readonly CursorAgentLimits limits = new() { SnapshotEvidenceLimit = 2, DefaultResponseTokenLimit = 256 };

    [Fact]
    public void BuildEvidenceSnapshot_TrimsToLimit()
    {
        var evidence = new List<EvidenceItem>
        {
            new("p1", "m1", "r1"),
            new("p2", "m2", "r2"),
            new("p3", "m3", "r3"),
        };
        var state = new CursorAgentState(evidence);
        var builder = new CursorAgentPromptBuilder(limits);

        var snapshot = builder.BuildEvidenceSnapshot(state);

        Assert.Contains("\"recentEvidencePointers\":[\"p2\",\"p3\"]", snapshot, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildBatchMessage_ReflectsPortion()
    {
        var portion = new CursorPortionView(
            new List<CursorItemView>
            {
                new("ptr1", "md1", "Paragraph"),
                new("ptr2", "md2", "Paragraph"),
            },
            true);
        var builder = new CursorAgentPromptBuilder(limits);

        var batch = builder.BuildBatchMessage(portion, 1);

        Assert.Contains("\"firstBatch\":false", batch, StringComparison.Ordinal);
        Assert.Contains("\"hasMoreBatches\":true", batch, StringComparison.Ordinal);
        Assert.Contains("ptr1", batch, StringComparison.Ordinal);
        Assert.Contains("ptr2", batch, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildTaskDefinitionPrompt_SkipsEmptyContext()
    {
        var request = new CursorAgentRequest("task", Context: " ");
        var builder = new CursorAgentPromptBuilder(limits);

        var prompt = builder.BuildTaskDefinitionPrompt(request);

        using var document = JsonDocument.Parse(prompt);
        var root = document.RootElement;
        Assert.True(root.TryGetProperty("context", out var contextElement));
        Assert.Equal(JsonValueKind.Null, contextElement.ValueKind);
    }

    [Fact]
    public void CreateSettings_UsesLimits()
    {
        var builder = new CursorAgentPromptBuilder(limits);

        var settings = builder.CreateSettings();

        Assert.Equal(limits.DefaultResponseTokenLimit, settings.MaxTokens);
        Assert.True(settings.ExtensionData!.ContainsKey("options"));
    }
}
