using AiTextEditor.Agent.CharacterBible;
using AiTextEditor.Agent.CharacterBible.Patching;
using AiTextEditor.Core.Model;
using Xunit;

namespace AiTextEditor.Tests;

public sealed class CharacterProfilePatchToolsTests
{
    [Fact]
    public void SetProfileField_AppliesValidFieldUpdate()
    {
        var (tools, session) = CreateTools();

        var result = tools.SetProfileField(
            CharacterBibleProfileField.Appearance,
            "Пахнет нафталином.",
            ["1.1.1.p4"]);

        Assert.Equal(SetProfileFieldResultStatus.Applied, result.Status);
        Assert.Equal("Пахнет нафталином.", session.GetRequired("c1").Profile!.Appearance);
    }

    [Fact]
    public void SetProfileField_RejectsEmptyValue()
    {
        var (tools, session) = CreateTools();

        var result = tools.SetProfileField(CharacterBibleProfileField.Appearance, "", ["1.1.1.p4"]);

        Assert.Equal(SetProfileFieldResultStatus.Rejected, result.Status);
        Assert.Null(session.GetRequired("c1").Profile!.Appearance);
    }

    [Fact]
    public void SetProfileField_RejectsPointerOutsideCurrentEvidence()
    {
        var (tools, session) = CreateTools();

        var result = tools.SetProfileField(
            CharacterBibleProfileField.Appearance,
            "Пахнет нафталином.",
            ["1.1.1.p999"]);

        Assert.Equal(SetProfileFieldResultStatus.Rejected, result.Status);
        Assert.Null(session.GetRequired("c1").Profile!.Appearance);
    }

    [Fact]
    public void SetProfileField_ReturnsNoOpForSameValue()
    {
        var (tools, session) = CreateTools(new CharacterProfile(Appearance: "Пахнет нафталином."));

        var result = tools.SetProfileField(
            CharacterBibleProfileField.Appearance,
            "Пахнет нафталином.",
            ["1.1.1.p4"]);

        Assert.Equal(SetProfileFieldResultStatus.NoOp, result.Status);
        Assert.Equal("Пахнет нафталином.", session.GetRequired("c1").Profile!.Appearance);
    }

    [Fact]
    public void SetProfileField_ReturnsConflictForDifferentExistingValue()
    {
        var (tools, session) = CreateTools(new CharacterProfile(PsychologicalProfile: "Склонен к накопительству."));

        var result = tools.SetProfileField(
            CharacterBibleProfileField.PsychologicalProfile,
            "Любит порядок.",
            ["1.1.1.p4"]);

        Assert.Equal(SetProfileFieldResultStatus.Conflict, result.Status);
        Assert.Equal("Склонен к накопительству.", session.GetRequired("c1").Profile!.PsychologicalProfile);
    }

    [Fact]
    public void SetProfileField_DoesNotExposeCharacterIdParameter()
    {
        var parameterNames = typeof(CharacterProfilePatchTools)
            .GetMethod(nameof(CharacterProfilePatchTools.SetProfileField))!
            .GetParameters()
            .Select(parameter => parameter.Name)
            .ToArray();

        Assert.DoesNotContain("characterId", parameterNames);
    }

    private static (CharacterProfilePatchTools Tools, CharacterDossierEditSession Session) CreateTools(
        CharacterProfile? profile = null)
    {
        var dossier = new CharacterDossier(
            "c1",
            "Пончик",
            [],
            new Dictionary<string, string>(),
            Profile: profile ?? CharacterProfile.Empty);
        var session = CharacterDossierEditSession.CreateFrom(new CharacterDossiers("d1", 1, [dossier]));
        var evidence = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["1.1.1.p4"] = "Пончик пахнет нафталином."
        };
        var context = new CharacterProfilePatchContext(
            profile,
            evidence.Keys.ToHashSet(StringComparer.Ordinal),
            evidence);
        var tools = new CharacterProfilePatchTools(
            "c1",
            "Пончик",
            context,
            session,
            new CharacterProfilePatchStatistics());
        return (tools, session);
    }
}
