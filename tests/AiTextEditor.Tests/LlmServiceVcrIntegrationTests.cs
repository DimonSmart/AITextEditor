using AiTextEditor.Lib.Model;
using AiTextEditor.Lib.Model.Intent;
using AiTextEditor.Lib.Services;
using AiTextEditor.Tests.Infrastructure;
using System.Text.Json;
using Xunit.Abstractions;

namespace AiTextEditor.Tests;

public class LlmServiceVcrIntegrationTests
{
    private readonly ITestOutputHelper output;

    public LlmServiceVcrIntegrationTests(ITestOutputHelper output)
    {
        this.output = output;
    }

    public static IEnumerable<object[]> IntentCommands => new[]
    {
    new object[] { "Add a TODO note to chapter two: verify all examples compile." },
    ["Rewrite the intro about monoliths vs microservices to be clearer for juniors."],

    ["In chapter one, tighten the opening paragraph so it grabs attention in the first two sentences."],
    ["After the section 'Why scaling is hard', insert a short real-world story about the 2019 billing outage."],
    ["Rename chapter three to 'Designing boundaries' and update any internal cross-references."],
    ["Drop the entire subsection titled 'Historical note' from chapter four; it's too distracting."],
    ["In the dependency injection chapter, rewrite the first code sample to use minimal APIs instead of old startup classes."],
    ["Wherever I rant about ORMs being evil, tone it down and make it sound more balanced and nuanced."],
    ["Before the summary of chapter six, add a checklist of five bullet points the reader should be able to do."],
    ["Take the paragraph that begins 'In a perfect world...' and move it up right after the first diagram in that chapter."],
    ["Replace the term 'junior developer' with 'early-career developer' across the whole book."],
    ["In the testing chapter, add a short example showing how to mock HttpClient properly."],
    ["Split the long list of cloud patterns into two separate tables: one for resiliency, one for cost optimization."],
    ["At the end of the CQRS section, add a warning box about overengineering for small teams."],
    ["Shorten the explanation of Big-O notation so it fits into a single concise paragraph."],
    ["In chapter eight, add a side note comparing Azure Functions and AWS Lambda, but keep it vendor-neutral in tone."],
    ["Change the heading 'Real story' to 'Case study' everywhere it appears."],
    ["Rewrite the conclusion to sound more like a friendly pep talk and less like release notes."],
    ["Add a one-page appendix with recommended learning paths for backend developers coming from Java."],
    ["In the 'Common pitfalls' chapter, turn the numbered list into a table with columns for symptom, cause, and fix."]
    };


    [Theory]
    [MemberData(nameof(IntentCommands))]
    public async Task IntentParser_WithRealLlm_ProducesStructuredIntent(string userCommand)
    {
        using var context = LlmTestHelper.CreateClient("intent-parser");
        var parser = new IntentParser(context.LlmClient);

        var result = await parser.ParseAsync(userCommand);

        output.WriteLine($"Raw intent response: {result.RawResponse}");
        if (result.Intent != null)
        {
            var payloadJson = JsonSerializer.Serialize(result.Intent.Payload.Fields);
            output.WriteLine($"Parsed intent: scope={result.Intent.ScopeType} payload={payloadJson}");
        }
        // These asserts stay permissive; verify richness/fields by reviewing the captured test logs above.

        Assert.True(result.Success, "LLM should return parsable intent.");
        Assert.NotNull(result.Intent);
        Assert.NotEqual(IntentScopeType.Unknown, result.Intent!.ScopeType);
    }

    [Fact]
    public async Task FunctionCallingLlmEditor_WithRealLlm_ReturnsOperations()
    {
        using var context = LlmTestHelper.CreateClient("llm-editor");
        var editor = new FunctionCallingLlmEditor(context.LlmClient);

        var document = new Document
        {
            Blocks =
            [
                new Block { Id = "h_intro", Type = BlockType.Heading, Level = 1, Numbering = "1", Markdown = "# Intro", PlainText = "Intro" },
                new Block { Id = "p_intro", Type = BlockType.Paragraph, Markdown = "Old intro", PlainText = "Old intro about architecture trade-offs." },
                new Block { Id = "p_body", Type = BlockType.Paragraph, Markdown = "Body paragraph", PlainText = "Body paragraph with details." }
            ]
        };

        var instruction = "Rewrite the intro paragraph (block id p_intro) to be punchy and inviting for new readers.";
        var ops = await editor.GetEditOperationsAsync(document.Blocks, "Improve the intro hook", instruction);

        output.WriteLine("Generated operations:");
        output.WriteLine(JsonSerializer.Serialize(ops, new JsonSerializerOptions { WriteIndented = true }));

        Assert.NotEmpty(ops);
        var knownIds = document.Blocks.Select(b => b.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains(ops, op => op.TargetBlockId != null && knownIds.Contains(op.TargetBlockId));
        Assert.True(ops.Any(op => op.NewBlock != null && !string.IsNullOrWhiteSpace(op.NewBlock.Markdown)),
            "Expected at least one operation to include new markdown content.");
    }
}
