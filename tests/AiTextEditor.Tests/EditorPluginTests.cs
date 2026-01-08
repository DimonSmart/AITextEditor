using AiTextEditor.Core.Services;
using AiTextEditor.Agent;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiTextEditor.Tests;

public class EditorPluginTests
{
    [Fact]
    public void CreateTargets_UsesArrayInputAndReturnsStructuredResponse()
    {
        var session = new EditorSession();
        session.LoadDefaultDocument("# Title\n\nParagraph one\n\nParagraph two");
        var context = new SemanticKernelContext();
        var plugin = new EditorPlugin(session, context, NullLogger<EditorPlugin>.Instance);

        var result = plugin.CreateTargets("label", new[] { "1.p1", "1.p2" });

        Assert.Equal(context.LastTargetSet?.Id, result.TargetSetId);
        Assert.Empty(result.InvalidPointers);
        Assert.Empty(result.Warnings);

        Assert.Collection(
            result.Targets,
            target =>
            {
                Assert.Equal("1.p1", target.Pointer.ToCompactString());
                Assert.Equal("Paragraph one", target.Excerpt);
            },
            target =>
            {
                Assert.Equal("1.p2", target.Pointer.ToCompactString());
                Assert.Equal("Paragraph two", target.Excerpt);
            });
    }

    [Fact]
    public void CreateTargets_ReportsDuplicatesAndInvalidPointers()
    {
        var session = new EditorSession();
        session.LoadDefaultDocument("# Title\n\nParagraph one\n\nParagraph two");
        var context = new SemanticKernelContext();
        var plugin = new EditorPlugin(session, context, NullLogger<EditorPlugin>.Instance);

        var result = plugin.CreateTargets("label", new[] { "1.p1", "1.p1", "missing", "  " });

        Assert.Single(result.Targets);
        Assert.Contains("missing", result.InvalidPointers);
        Assert.Contains("  ", result.InvalidPointers);
        Assert.Single(result.Warnings);
        Assert.Contains("Duplicate pointer ignored", result.Warnings[0]);
        Assert.NotNull(context.LastTargetSet);
    }
}
