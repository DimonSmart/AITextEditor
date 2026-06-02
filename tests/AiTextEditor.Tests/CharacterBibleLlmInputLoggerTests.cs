using AiTextEditor.Agent.CharacterBible.Diagnostics;
using AiTextEditor.Agent.CharacterBible.Extraction;
using AiTextEditor.Agent.CharacterBible.Patching;
using AiTextEditor.Agent.CharacterBible;
using Xunit;

namespace AiTextEditor.Tests;

public sealed class CharacterBibleLlmInputLoggerTests
{
    [Fact]
    public void DebugInput_WritesFullDynamicModelWithoutShortTextTruncation()
    {
        var paragraphText = "Пончик вошёл. " + new string('я', 600);
        var input = new CharacterExtractionPromptBuilder().BuildPromptInput(
            [("1.1.1.p3", paragraphText)]);
        var logger = new CapturingCharacterBibleRunLogger();

        using (CharacterBibleRunLogScope.Push(logger))
        {
            CharacterBibleLlmInputLogger.DebugInput(
                "extract.llm.input",
                "batchIndex=1 paragraphCount=1",
                input);
        }

        var block = Assert.Single(logger.Blocks);
        Assert.Equal("extract.llm.input", block.EventName);
        Assert.Contains("\"pointer\": \"1.1.1.p3\"", block.Body, StringComparison.Ordinal);
        Assert.Contains(paragraphText, block.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("...", block.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void DebugProfileUpdateContract_ReportsExpectedShape()
    {
        var logger = new CapturingCharacterBibleRunLogger();
        var input = new CharacterProfileUpdatePromptInput(
            new CharacterProfileUpdateTarget("Пончик"),
            new CharacterProfileUpdateCurrentProfile(null, null, null, null),
            [new CharacterProfileUpdateEvidence("1.1.1.p3", "Пончик вошёл.")]);

        using (CharacterBibleRunLogScope.Push(logger))
        {
            CharacterBibleLlmInputLogger.DebugProfileUpdateContract(input);
        }

        var diagnostic = Assert.Single(logger.DebugMessages);
        Assert.Equal("profile.update.llm.input.contract", diagnostic.EventName);
        Assert.Contains("topLevelKeys=[\"target\", \"currentProfile\", \"newEvidence\"]", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("forbiddenKeysFound=[]", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("evidenceCount=1", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("emptyEvidenceTexts=[]", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DebugInput_PreservesExplicitNullProfileFields()
    {
        var logger = new CapturingCharacterBibleRunLogger();
        var input = new CharacterProfileUpdatePromptInput(
            new CharacterProfileUpdateTarget("Пончик"),
            new CharacterProfileUpdateCurrentProfile(null, null, null, null),
            [new CharacterProfileUpdateEvidence("1.1.1.p3", "Пончик вошёл.")]);

        using (CharacterBibleRunLogScope.Push(logger))
        {
            CharacterBibleLlmInputLogger.DebugInput("profile.update.llm.input", "characterId=c1", input);
        }

        var block = Assert.Single(logger.Blocks);
        Assert.Contains("\"appearance\": null", block.Body, StringComparison.Ordinal);
        Assert.Contains("\"psychologicalProfile\": null", block.Body, StringComparison.Ordinal);
    }

    private sealed class CapturingCharacterBibleRunLogger : ICharacterBibleRunLogger
    {
        public CharacterBibleRunLogContext Context { get; } = new(
            "test",
            "test.log",
            DateTimeOffset.UnixEpoch);

        public List<(string EventName, string Header, string Body)> Blocks { get; } = [];

        public List<(string EventName, string Message)> DebugMessages { get; } = [];

        public void Info(string eventName, string message)
        {
        }

        public void Debug(string eventName, string message)
        {
            DebugMessages.Add((eventName, message));
        }

        public void DebugBlock(string eventName, string header, string block)
        {
            Blocks.Add((eventName, header, block));
        }

        public void Warning(string eventName, string message)
        {
        }

        public void Error(string eventName, string message, Exception? exception = null)
        {
        }
    }
}

