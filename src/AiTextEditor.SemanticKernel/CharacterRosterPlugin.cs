using System.ComponentModel;
using AiTextEditor.Lib.Model;
using AiTextEditor.Lib.Services;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace AiTextEditor.SemanticKernel;

public sealed class CharacterRosterPlugin
{
    private readonly CharacterRosterGenerator _generator;
    private readonly CharacterRosterService _rosterService;
    private readonly CursorAgentLimits _limits;
    private readonly ILogger<CharacterRosterPlugin> _logger;

    public CharacterRosterPlugin(
        CharacterRosterGenerator generator,
        CharacterRosterService rosterService,
        CursorAgentLimits limits,
        ILogger<CharacterRosterPlugin> logger)
    {
        _generator = generator ?? throw new ArgumentNullException(nameof(generator));
        _rosterService = rosterService ?? throw new ArgumentNullException(nameof(rosterService));
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    [KernelFunction("generate_character_roster")]
    [Description("Fast scan of the document and build a compact character roster from detected name mentions.")]
    public Task<CharacterRosterCommandResult> GenerateCharacterRosterAsync(CancellationToken cancellationToken = default)
    {
        return GenerateAsync(RosterDetailLevel.Roster, cancellationToken);
    }

    [KernelFunction("generate_character_dossiers")]
    [Description("Scan the document and build a detailed character dossier catalog (slower, uses LLM).")]
    public Task<CharacterRosterCommandResult> GenerateCharacterDossiersAsync(CancellationToken cancellationToken = default)
    {
        return GenerateAsync(RosterDetailLevel.Dossiers, cancellationToken);
    }

    [KernelFunction("get_character_roster")]
    [Description("Return the current character roster (name, description, gender, aliases, firstPointer).")]
    public CharacterRosterPayload GetCharacterRoster()
    {
        var roster = _rosterService.GetRoster();
        var characters = roster.Characters
            .Select(c => new CharacterRosterEntry(
                c.CharacterId,
                c.Name,
                c.Description,
                c.Gender,
                c.Aliases,
                c.FirstPointer))
            .ToList();

        return new CharacterRosterPayload(roster.RosterId, roster.Version, characters);
    }

    [KernelFunction("upsert_character")]
    [Description("Add or update a character entry manually.")]
    public CharacterRosterCommandResult UpsertCharacter(
        string name,
        string description = "",
        string gender = "unknown",
        string[]? aliases = null,
        string? firstPointer = null,
        string? characterId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var normalizedAliases = aliases?
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a.Trim())
            .ToList() ?? new List<string>();

        var id = string.IsNullOrWhiteSpace(characterId) ? Guid.NewGuid().ToString("N") : characterId.Trim();
        var profile = new CharacterProfile(
            id,
            name.Trim(),
            description?.Trim() ?? string.Empty,
            normalizedAliases,
            string.IsNullOrWhiteSpace(firstPointer) ? null : firstPointer.Trim(),
            string.IsNullOrWhiteSpace(gender) ? "unknown" : gender.Trim());

        var saved = _rosterService.UpsertCharacter(profile);
        var roster = _rosterService.GetRoster();
        _logger.LogInformation("Character upserted: {CharacterId}", saved.CharacterId);

        return new CharacterRosterCommandResult(roster.RosterId, roster.Version, saved.CharacterId);
    }

    [KernelFunction("refresh_character_roster")]
    [Description("Refresh the character roster. Provide changed semantic pointers to re-index partially, or omit to rebuild fully.")]
    public Task<CharacterRosterCommandResult> RefreshCharacterRosterAsync(
        string[]? changedPointers = null,
        CancellationToken cancellationToken = default)
    {
        return RefreshAsync(RosterDetailLevel.Roster, changedPointers, cancellationToken);
    }

    [KernelFunction("refresh_character_dossiers")]
    [Description("Refresh the character dossiers. Provide changed semantic pointers to re-index partially, or omit to rebuild fully.")]
    public Task<CharacterRosterCommandResult> RefreshCharacterDossiersAsync(
        string[]? changedPointers = null,
        CancellationToken cancellationToken = default)
    {
        return RefreshAsync(RosterDetailLevel.Dossiers, changedPointers, cancellationToken);
    }

    private async Task<CharacterRosterCommandResult> GenerateAsync(RosterDetailLevel detailLevel, CancellationToken cancellationToken)
    {
        var roster = detailLevel == RosterDetailLevel.Dossiers
            ? await _generator.GenerateDossiersAsync(cancellationToken)
            : await _generator.GenerateAsync(cancellationToken);

        _logger.LogInformation("Character roster generated: {RosterId} v{Version}", roster.RosterId, roster.Version);
        return new CharacterRosterCommandResult(roster.RosterId, roster.Version);
    }

    private async Task<CharacterRosterCommandResult> RefreshAsync(
        RosterDetailLevel detailLevel,
        string[]? changedPointers,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizePointers(changedPointers);
        if (normalized.Count == 0)
        {
            return await GenerateAsync(detailLevel, cancellationToken);
        }

        if (normalized.Count > _limits.MaxElements)
        {
            _logger.LogInformation("RefreshCharacterRoster: too many pointers ({Count}), running full generation.", normalized.Count);
            return await GenerateAsync(detailLevel, cancellationToken);
        }

        CharacterRoster roster = detailLevel == RosterDetailLevel.Dossiers
            ? await _generator.RefreshDossiersAsync(normalized, cancellationToken)
            : await _generator.RefreshAsync(normalized, cancellationToken);

        return new CharacterRosterCommandResult(roster.RosterId, roster.Version);
    }

    private static List<string> NormalizePointers(string[]? changedPointers)
    {
        return changedPointers?
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList() ?? new List<string>();
    }

    private enum RosterDetailLevel
    {
        Roster,
        Dossiers
    }
}
