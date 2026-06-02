using AiTextEditor.Core.Model;
using AiTextEditor.Core.Services;
using Xunit;

namespace AiTextEditor.Tests;

public sealed class CharacterDossierIdTests
{
    [Fact]
    public void CreateCharacter_FirstCharacterUsesOne()
    {
        var service = new CharacterDossierService("d1");

        var character = service.CreateCharacter(Draft("Alice"));

        Assert.Equal(1, character.CharacterId);
        Assert.Equal(2, service.GetDossiers().NextCharacterId);
    }

    [Fact]
    public void CreateCharacter_MultipleCharactersUseSequentialIds()
    {
        var service = new CharacterDossierService("d1");

        var characterIds = new[]
        {
            service.CreateCharacter(Draft("Alice")).CharacterId,
            service.CreateCharacter(Draft("Bob")).CharacterId,
            service.CreateCharacter(Draft("Charlie")).CharacterId
        };

        Assert.Equal([1, 2, 3], characterIds);
        Assert.Equal(4, service.GetDossiers().NextCharacterId);
    }

    [Fact]
    public void CreateCharacter_UsesNextCharacterIdInsteadOfCharacterCount()
    {
        var service = new CharacterDossierService("d1");
        service.ReplaceDossiers(new CharacterDossiers(
            "d1",
            CharacterDossierService.CurrentVersion,
            [Character(1, "Alice"), Character(2, "Bob"), Character(3, "Charlie")],
            NextCharacterId: 10));

        var character = service.CreateCharacter(Draft("Dora"));

        Assert.Equal(10, character.CharacterId);
        Assert.Equal(11, service.GetDossiers().NextCharacterId);
    }

    [Fact]
    public void RemoveDossier_DoesNotReuseCharacterId()
    {
        var service = new CharacterDossierService("d1");
        service.CreateCharacter(Draft("Alice"));
        service.CreateCharacter(Draft("Bob"));

        service.RemoveDossier(2);
        var character = service.CreateCharacter(Draft("Charlie"));

        Assert.Equal(3, character.CharacterId);
        Assert.Equal(4, service.GetDossiers().NextCharacterId);
    }

    [Fact]
    public void LoadFromJson_RejectsLegacyStringIds()
    {
        var service = new CharacterDossierService("empty");

        Assert.ThrowsAny<Exception>(() => service.LoadFromJson(
            """
            {
              "dossiersId": "d1",
              "version": 2,
              "nextCharacterId": 2,
              "characters": [
                {
                  "characterId": "old-a",
                  "name": "Alice",
                  "aliases": [],
                  "aliasExamples": {}
                }
              ]
            }
            """));
    }

    [Fact]
    public void LoadFromJson_MissingNextCharacterId_MigratesFromMaxCharacterId()
    {
        var service = new CharacterDossierService("empty");

        service.LoadFromJson(
            """
            {
              "dossiersId": "d1",
              "version": 3,
              "characters": [
                {
                  "characterId": 5,
                  "name": "Alice",
                  "aliases": [],
                  "aliasExamples": {}
                }
              ]
            }
            """);

        Assert.Equal(6, service.GetDossiers().NextCharacterId);
    }

    [Fact]
    public void LoadFromJson_MissingNextCharacterId_UsesOneForEmptyArchive()
    {
        var service = new CharacterDossierService("empty");

        service.LoadFromJson(
            """
            {
              "dossiersId": "d1",
              "version": 3,
              "characters": []
            }
            """);

        Assert.Equal(1, service.GetDossiers().NextCharacterId);
    }

    [Fact]
    public void SaveToJson_LoadFromJson_PreservesAdvancedNextCharacterId()
    {
        var source = new CharacterDossierService("d1");
        source.ReplaceDossiers(new CharacterDossiers(
            "d1",
            CharacterDossierService.CurrentVersion,
            [Character(1, "Alice"), Character(2, "Bob"), Character(3, "Charlie")],
            NextCharacterId: 6));

        var character = source.CreateCharacter(Draft("Dora"));
        var target = new CharacterDossierService("empty");
        target.LoadFromJson(source.SaveToJson());

        Assert.Equal(6, character.CharacterId);
        Assert.Equal(7, source.GetDossiers().NextCharacterId);
        Assert.Equal(7, target.GetDossiers().NextCharacterId);
    }

    [Fact]
    public void LoadFromJson_RejectsOldSchemaVersion()
    {
        var service = new CharacterDossierService("empty");

        var exception = Assert.Throws<InvalidOperationException>(() => service.LoadFromJson(
            """
            {
              "dossiersId": "d1",
              "version": 2,
              "nextCharacterId": 1,
              "characters": []
            }
            """));

        Assert.Equal("Character dossiers version must be 3.", exception.Message);
    }

    [Fact]
    public void LoadFromJson_RejectsDuplicateCharacterIds()
    {
        var service = new CharacterDossierService("empty");

        var exception = Assert.Throws<InvalidOperationException>(() => service.LoadFromJson(
            """
            {
              "dossiersId": "d1",
              "version": 3,
              "nextCharacterId": 3,
              "characters": [
                {
                  "characterId": 1,
                  "name": "Alice",
                  "aliases": [],
                  "aliasExamples": {}
                },
                {
                  "characterId": 1,
                  "name": "Bob",
                  "aliases": [],
                  "aliasExamples": {}
                }
              ]
            }
            """));

        Assert.Contains("duplicate CharacterId", exception.Message, StringComparison.Ordinal);
        Assert.Contains("1", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReplaceDossiers_RejectsDuplicateCharacterIds()
    {
        var service = new CharacterDossierService("d1");

        var exception = Assert.Throws<InvalidOperationException>(() => service.ReplaceDossiers(new CharacterDossiers(
            "d1",
            CharacterDossierService.CurrentVersion,
            [Character(1, "Alice"), Character(1, "Bob")],
            NextCharacterId: 3)));

        Assert.Contains("duplicate CharacterId", exception.Message, StringComparison.Ordinal);
        Assert.Contains("1", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CharacterNameIndex_ReturnsEveryDuplicateNormalizedName()
    {
        var index = new CharacterNameIndex(
            [Character(1, "Alice"), Character(2, " alice ")]);

        Assert.Equal([1, 2], index.FindByName("ALICE"));
    }


    private static NewCharacterDraft Draft(string name)
        => new(name, new Dictionary<string, string>());

    private static CharacterDossier Character(int characterId, string name)
        => new(characterId, name, [], new Dictionary<string, string>());
}
