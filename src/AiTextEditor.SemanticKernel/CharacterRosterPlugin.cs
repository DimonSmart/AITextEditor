using System.ComponentModel;
using System.Text.Json;
using AiTextEditor.Lib.Common;
using AiTextEditor.Lib.Model;
using AiTextEditor.Lib.Services;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace AiTextEditor.SemanticKernel;

public sealed class CharacterRosterPlugin(
    CharacterRosterGenerator generator,
    CharacterRosterService rosterService,
    CursorAgentLimits limits,
    ILogger<CharacterRosterPlugin> logger)
{
    private readonly CharacterRosterGenerator generator = generator ?? throw new ArgumentNullException(nameof(generator));
    private readonly CharacterRosterService rosterService = rosterService ?? throw new ArgumentNullException(nameof(rosterService));
    private readonly CursorAgentLimits limits = limits ?? throw new ArgumentNullException(nameof(limits));
    private readonly ILogger<CharacterRosterPlugin> logger = logger ?? throw new ArgumentNullException(nameof(logger));

    [KernelFunction("generate_character_roster")]
    [Description("Scan the document and build a character roster from detected name mentions.")]
    public async Task<object> GenerateCharacterRosterAsync(CancellationToken cancellationToken = default)
    {
        var roster = await generator.GenerateAsync(cancellationToken);
        logger.LogInformation("Character roster generated: {RosterId} v{Version}", roster.RosterId, roster.Version);
        return new { rosterId = roster.RosterId, version = roster.Version };
    }

    [KernelFunction("get_character_roster")]
    [Description("Return the current character roster as compact JSON (name, description, aliases, firstPointer).")]
    public string GetCharacterRoster()
    {
        var roster = rosterService.GetRoster();
        var payload = new
        {
            rosterId = roster.RosterId,
            version = roster.Version,
            characters = roster.Characters.Select(c => new
            {
                id = c.CharacterId,
                name = c.Name,
                description = c.Description,
                aliases = c.Aliases,
                firstPointer = c.FirstPointer
            })
        };

        return JsonSerializer.Serialize(payload, SerializationOptions.RelaxedCompact);
    }

    [KernelFunction("upsert_character")]
    [Description("Add or update a character entry manually.")]
    public object UpsertCharacter(
        string name,
        string description = "",
        string[]? aliases = null,
        string? firstPointer = null,
        string? characterId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var normalizedAliases = aliases?.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).ToList() ?? new List<string>();
        var id = string.IsNullOrWhiteSpace(characterId) ? Guid.NewGuid().ToString("N") : characterId.Trim();
        var profile = new CharacterProfile(
            id,
            name.Trim(),
            description?.Trim() ?? string.Empty,
            normalizedAliases,
            string.IsNullOrWhiteSpace(firstPointer) ? null : firstPointer.Trim());

        var saved = rosterService.UpsertCharacter(profile);
        logger.LogInformation("Character upserted: {CharacterId}", saved.CharacterId);

        return new { rosterId = rosterService.GetRoster().RosterId, version = rosterService.GetRoster().Version, characterId = saved.CharacterId };
    }

    [KernelFunction("refresh_character_roster")]
    [Description("Refresh the character roster. Provide changed semantic pointers to re-index partially, or omit to rebuild fully.")]
    public async Task<object> RefreshCharacterRosterAsync(
        string[]? changedPointers = null,
        CancellationToken cancellationToken = default)
    {
        var pointerCount = changedPointers?.Count(p => !string.IsNullOrWhiteSpace(p)) ?? 0;
        CharacterRoster roster;

        if (pointerCount == 0)
        {
            roster = await generator.GenerateAsync(cancellationToken);
        }
        else if (pointerCount > limits.MaxElements)
        {
            logger.LogInformation("RefreshCharacterRoster: too many pointers ({Count}), running full generation.", pointerCount);
            roster = await generator.GenerateAsync(cancellationToken);
        }
        else
        {
            roster = await generator.RefreshAsync(changedPointers!, cancellationToken);
        }

        return new { rosterId = roster.RosterId, version = roster.Version };
    }
}
