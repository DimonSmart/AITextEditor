using System.Globalization;
using System.Text.Json;
using AiTextEditor.Lib.Common;
using AiTextEditor.Lib.Model;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace AiTextEditor.Lib.Services;

public sealed class CharacterRosterService
{
    private readonly object syncRoot = new();
    private CharacterRoster roster;
    private readonly IDeserializer yamlDeserializer;
    private readonly ISerializer yamlSerializer;

    public CharacterRosterService()
    {
        yamlSerializer = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();

        yamlDeserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();

        roster = CreateEmptyRoster();
    }

    public CharacterRoster GetRoster()
    {
        lock (syncRoot)
        {
            return roster;
        }
    }

    public CharacterProfile UpsertCharacter(CharacterProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(profile.CharacterId);
        ArgumentException.ThrowIfNullOrWhiteSpace(profile.Name);

        lock (syncRoot)
        {
            var characters = roster.Characters.ToDictionary(c => c.CharacterId, StringComparer.Ordinal);
            characters[profile.CharacterId] = NormalizeProfile(profile);

            roster = new CharacterRoster(roster.RosterId, roster.Version + 1, characters.Values.ToList());
            return characters[profile.CharacterId];
        }
    }

    public bool RemoveCharacter(string characterId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(characterId);

        lock (syncRoot)
        {
            var existing = roster.Characters.FirstOrDefault(c => string.Equals(c.CharacterId, characterId, StringComparison.Ordinal));
            if (existing == null)
            {
                return false;
            }

            var updated = roster.Characters.Where(c => !string.Equals(c.CharacterId, characterId, StringComparison.Ordinal)).ToList();
            roster = new CharacterRoster(roster.RosterId, roster.Version + 1, updated);
            return true;
        }
    }

    public void ReplaceRoster(IReadOnlyCollection<CharacterProfile> characters)
    {
        ArgumentNullException.ThrowIfNull(characters);

        lock (syncRoot)
        {
            var normalized = characters.Select(NormalizeProfile).ToList();
            roster = new CharacterRoster(roster.RosterId, roster.Version + 1, normalized);
        }
    }

    public string SaveToJson()
    {
        lock (syncRoot)
        {
            return JsonSerializer.Serialize(roster, SerializationOptions.RelaxedCompact);
        }
    }

    public string SaveToYaml()
    {
        lock (syncRoot)
        {
            return yamlSerializer.Serialize(roster);
        }
    }

    public void LoadFromJson(string payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);

        var parsed = JsonSerializer.Deserialize<CharacterRoster>(payload, SerializationOptions.RelaxedCompact);
        if (parsed == null)
        {
            throw new InvalidOperationException("Failed to deserialize character roster from JSON.");
        }

        ApplyLoadedRoster(parsed);
    }

    public void LoadFromYaml(string payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);

        var parsed = yamlDeserializer.Deserialize<CharacterRoster>(payload);
        if (parsed == null)
        {
            throw new InvalidOperationException("Failed to deserialize character roster from YAML.");
        }

        ApplyLoadedRoster(parsed);
    }

    private void ApplyLoadedRoster(CharacterRoster loaded)
    {
        var loadedCharacters = loaded.Characters ?? Array.Empty<CharacterProfile>();
        var normalizedCharacters = loadedCharacters.Select(NormalizeProfile).ToList();
        if (string.IsNullOrWhiteSpace(loaded.RosterId))
        {
            throw new InvalidOperationException("RosterId cannot be empty.");
        }

        lock (syncRoot)
        {
            roster = new CharacterRoster(
                loaded.RosterId,
                loaded.Version > 0 ? loaded.Version : 1,
                normalizedCharacters);
        }
    }

    private static CharacterProfile NormalizeProfile(CharacterProfile profile)
    {
        var aliases = profile.Aliases
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(a => a, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return profile with
        {
            CharacterId = profile.CharacterId.Trim(),
            Name = profile.Name.Trim(),
            Description = profile.Description?.Trim() ?? string.Empty,
            Aliases = aliases,
            Gender = NormalizeGender(profile.Gender)
        };
    }

    private static string NormalizeGender(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "unknown";
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "male" or "m" or "man" => "male",
            "female" or "f" or "woman" => "female",
            "unknown" => "unknown",
            _ => "unknown"
        };
    }

    private static CharacterRoster CreateEmptyRoster()
    {
        return new CharacterRoster(
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
            1,
            Array.Empty<CharacterProfile>());
    }
}
