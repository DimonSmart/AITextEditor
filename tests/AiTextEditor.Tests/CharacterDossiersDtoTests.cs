using System.Text.Json;
using AiTextEditor.Agent.CharacterBible;
using AiTextEditor.Core.Model;
using Xunit;

namespace AiTextEditor.Tests;

public sealed class CharacterDossiersDtoTests
{
    [Fact]
    public void CharacterDossiersPayload_SerializesCharacterId()
    {
        var payload = new CharacterDossiersPayload(
            "d1",
            3,
            [
                new CharacterDossierEntry(
                    1,
                    "Alice",
                    "unknown",
                    7,
                    CharacterProfile.Empty,
                    [],
                    new Dictionary<string, string>())
            ]);

        var json = JsonSerializer.Serialize(payload);
        using var document = JsonDocument.Parse(json);
        var character = document.RootElement.GetProperty("characters").EnumerateArray().Single();

        Assert.True(character.TryGetProperty("characterId", out _));
        Assert.False(character.TryGetProperty("id", out _));
    }
}
