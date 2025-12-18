using AiTextEditor.Lib.Model;
using AiTextEditor.SemanticKernel;
using Xunit;

namespace AiTextEditor.Domain.Tests;

public class CursorAgentResponseParserTests
{
    private readonly CursorAgentResponseParser parser = new();

    [Fact]
    public void ParseCommand_HandlesSanitizedPayload()
    {
        var content = "Lead text {\"decision\":\"continue\",\"newEvidence\":[{\"pointer\":\"p1\",\"excerpt\":\"line1\nline2\",\"reason\":\"match\"}],\"progress\":\"keep going\"} trailing";

        var result = parser.ParseCommand(content, out var parsedFragment, out var multipleActions, out var finishDetected);

        Assert.NotNull(result);
        Assert.Equal("continue", result!.Decision);
        Assert.False(multipleActions);
        Assert.False(finishDetected);
        Assert.Equal("p1", result.NewEvidence![0].Pointer);
        Assert.Equal("line1\nline2", result.NewEvidence![0].Excerpt);
        Assert.Equal("keep going", result.Progress);
        Assert.Contains("\\n", parsedFragment, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseFinalizer_ReturnsNullForInvalidJson()
    {
        var result = parser.ParseFinalizer("<not json>");

        Assert.Null(result);
    }

    [Fact]
    public void ParseFinalizer_ReadsSuccessPayload()
    {
        var json = "{" +
                   "\"decision\":\"success\"," +
                   "\"semanticPointerFrom\":\"ptr\"," +
                   "\"excerpt\":\"excerpt text\"," +
                   "\"whyThis\":\"because\"," +
                   "\"markdown\":\"md\"," +
                   "\"summary\":\"summary text\"}";

        var result = parser.ParseFinalizer(json);

        Assert.NotNull(result);
        Assert.Equal("success", result!.Decision);
        Assert.Equal("ptr", result.SemanticPointerFrom);
        Assert.Equal("excerpt text", result.Excerpt);
        Assert.Equal("because", result.WhyThis);
        Assert.Equal("md", result.Markdown);
        Assert.Equal("summary text", result.Summary);
    }
}
