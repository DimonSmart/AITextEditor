using System.ComponentModel;
using AiTextEditor.Lib.Model;
using AiTextEditor.Lib.Services;
using System.Linq;
using Microsoft.SemanticKernel;

namespace AiTextEditor.SemanticKernel;

public sealed class CharacterRosterFunctionCollection(CharacterRosterService rosterService)
{
    private readonly CharacterRosterService rosterService = rosterService ?? throw new ArgumentNullException(nameof(rosterService));

    [KernelFunction("find_character_candidates")]
    [Description("Find characters whose canonical name or aliases match the provided name.")]
    public IReadOnlyCollection<CharacterRosterEntry> FindCharacterCandidates(
        [Description("Name or alias to resolve. Case-insensitive.")] string nameOrAlias)
    {
        var matches = rosterService.FindByNameOrAlias(nameOrAlias);
        return matches
            .Select(ToEntry)
            .ToList();
    }

    [KernelFunction("get_character_profile")]
    [Description("Get a character profile by id.")]
    public CharacterRosterEntry? GetCharacterProfile(
        [Description("Character id to fetch.")] string characterId)
    {
        var profile = rosterService.TryGetCharacter(characterId);
        return profile == null ? null : ToEntry(profile);
    }

    [KernelFunction("upsert_character_profile")]
    [Description("Create or update a character profile. If characterId is empty, a new character is created.")]
    public CharacterRosterEntry UpsertCharacterProfile(
        [Description("Canonical character name. Required when creating a new character.")] string name,
        [Description("Brief description of the character.")] string description,
        [Description("Gender value: male, female, or unknown.")] string gender = "unknown",
        [Description("Optional aliases for the character.")] string[]? aliases = null,
        [Description("Existing character id to update, leave empty to create.")] string? characterId = null)
    {
        var normalizedId = string.IsNullOrWhiteSpace(characterId)
            ? Guid.NewGuid().ToString("N")
            : characterId.Trim();

        var normalizedAliases = aliases?
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a.Trim())
            .ToList() ?? [];

        var profile = new CharacterProfile(
            normalizedId,
            name,
            description ?? string.Empty,
            normalizedAliases,
            string.IsNullOrWhiteSpace(gender) ? "unknown" : gender);

        var saved = rosterService.UpsertCharacter(profile);
        return ToEntry(saved);
    }

    private static CharacterRosterEntry ToEntry(CharacterProfile profile)
    {
        return new CharacterRosterEntry(
            profile.CharacterId,
            profile.Name,
            profile.Description,
            profile.Gender,
            profile.Aliases);
    }
}
