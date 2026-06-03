using AiTextEditor.Agent.CharacterBible;
using AiTextEditor.Agent.CharacterBible.Diagnostics;
using AiTextEditor.Agent.CharacterBible.Patching;
using AiTextEditor.Core.Model;
using Xunit;

namespace AiTextEditor.Tests;

public sealed class CharacterProfileUpdateToolAdapterTests
{
    [Fact]
    public void ReplaceProfileField_AppliesValidFieldUpdate()
    {
        var (tools, session) = CreateTools();

        var result = tools.ReplaceProfileField(
            CharacterBibleProfileField.Appearance,
            "Пахнет нафталином.");

        Assert.Equal(ReplaceProfileFieldResultStatus.Applied, result.Status);
        Assert.Equal("Пахнет нафталином.", session.GetRequired(1).Profile!.Appearance);
    }

    [Fact]
    public void ReplaceProfileField_RejectsEmptyValue()
    {
        var (tools, session) = CreateTools();

        var result = tools.ReplaceProfileField(CharacterBibleProfileField.Appearance, "");

        Assert.Equal(ReplaceProfileFieldResultStatus.Rejected, result.Status);
        Assert.Null(session.GetRequired(1).Profile!.Appearance);
    }

    [Fact]
    public void ReplaceProfileField_ExposesOnlyFieldAndValueParameters()
    {
        var parameterNames = typeof(CharacterProfileUpdateToolAdapter)
            .GetMethod(nameof(CharacterProfileUpdateToolAdapter.ReplaceProfileField))!
            .GetParameters()
            .Select(parameter => parameter.Name)
            .ToArray();

        Assert.Equal(["field", "value"], parameterNames);
        Assert.DoesNotContain("reason", parameterNames);
        Assert.DoesNotContain("status", parameterNames);
        Assert.DoesNotContain("evidencePointers", parameterNames);
    }

    [Fact]
    public void ReplaceProfileField_ReturnsNoOpForSameValue()
    {
        var (tools, session) = CreateTools(new CharacterProfile(Appearance: "Пахнет нафталином."));

        var result = tools.ReplaceProfileField(
            CharacterBibleProfileField.Appearance,
            "Пахнет нафталином.");

        Assert.Equal(ReplaceProfileFieldResultStatus.NoOp, result.Status);
        Assert.Equal("Пахнет нафталином.", session.GetRequired(1).Profile!.Appearance);
    }

    [Fact]
    public void ReplaceProfileField_ReplacesDifferentExistingValue()
    {
        var (tools, session) = CreateTools(new CharacterProfile(PsychologicalProfile: "Склонен к накопительству."));

        var result = tools.ReplaceProfileField(
            CharacterBibleProfileField.PsychologicalProfile,
            "Любит порядок.");

        Assert.Equal(ReplaceProfileFieldResultStatus.Applied, result.Status);
        Assert.Equal("Любит порядок.", session.GetRequired(1).Profile!.PsychologicalProfile);
    }

    [Fact]
    public void ReplaceProfileField_DoesNotExposeCharacterIdParameter()
    {
        var parameterNames = typeof(CharacterProfileUpdateToolAdapter)
            .GetMethod(nameof(CharacterProfileUpdateToolAdapter.ReplaceProfileField))!
            .GetParameters()
            .Select(parameter => parameter.Name)
            .ToArray();

        Assert.DoesNotContain("characterId", parameterNames);
    }

    [Fact]
    public void ReplaceProfileField_LogsCharacterIdForCharacterScopedToolCalls()
    {
        var logger = new CapturingCharacterBibleRunLogger();
        var (tools, _) = CreateTools();

        using (CharacterBibleRunLogScope.Push(logger))
        {
            tools.ReplaceProfileField(
                CharacterBibleProfileField.StatusAndCompetence,
                "Умеет считать.");
        }

        Assert.Contains(logger.InfoMessages, message =>
            message.EventName == "profile.update.tool.call"
            && message.Message.Contains("characterId=1", StringComparison.Ordinal)
            && message.Message.Contains("name=\"Пончик\"", StringComparison.Ordinal));
        Assert.Contains(logger.DebugMessages, message =>
            message.EventName == "profile.update.tool.value"
            && message.Message.Contains("characterId=1", StringComparison.Ordinal)
            && message.Message.Contains("name=\"Пончик\"", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.InfoMessages.Concat(logger.DebugMessages), message =>
            message.Message.Contains("character=\"", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.InfoMessages, message =>
            message.EventName == "profile.update.tool.call"
            && (message.Message.Contains("reason=", StringComparison.Ordinal)
                || message.Message.Contains("status=", StringComparison.Ordinal)
                || message.Message.Contains("evidencePointers=", StringComparison.Ordinal)));
    }

    private static (CharacterProfileUpdateToolAdapter Tools, CharacterDossierEditSession Session) CreateTools(
        CharacterProfile? profile = null)
    {
        var dossier = new CharacterDossier(
            1,
            "Пончик",
            [],
            new Dictionary<string, string>(),
            Profile: profile ?? CharacterProfile.Empty);
        var session = CharacterDossierEditSession.CreateFrom(new CharacterDossiers("d1", 1, [dossier]));
        var context = new CharacterProfileUpdateContext(profile);
        var tools = new CharacterProfileUpdateToolAdapter(
            1,
            "Пончик",
            context,
            session,
            new CharacterProfileUpdateStatistics());
        return (tools, session);
    }

    private sealed class CapturingCharacterBibleRunLogger : ICharacterBibleRunLogger
    {
        public CharacterBibleRunLogContext Context { get; } = new(
            "test",
            "test.log",
            DateTimeOffset.UnixEpoch);

        public List<(string EventName, string Message)> InfoMessages { get; } = [];

        public List<(string EventName, string Message)> DebugMessages { get; } = [];

        public void Info(string eventName, string message)
        {
            InfoMessages.Add((eventName, message));
        }

        public void Debug(string eventName, string message)
        {
            DebugMessages.Add((eventName, message));
        }

        public void DebugBlock(string eventName, string header, string block)
        {
        }

        public void Warning(string eventName, string message)
        {
        }

        public void Error(string eventName, string message, Exception? exception = null)
        {
        }
    }
}

