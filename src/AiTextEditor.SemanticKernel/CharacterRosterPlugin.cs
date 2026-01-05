using System.ComponentModel;
using AiTextEditor.Lib.Model;
using AiTextEditor.Lib.Services;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace AiTextEditor.SemanticKernel;

public sealed class CharacterRosterPlugin
{
    private readonly CharacterRosterGenerator _generator;
    private readonly CharacterRosterCursorOrchestrator _orchestrator;
    private readonly CharacterRosterService _rosterService;
    private readonly CursorAgentLimits _limits;
    private readonly ILogger<CharacterRosterPlugin> _logger;

    public CharacterRosterPlugin(
        CharacterRosterGenerator generator,
        CharacterRosterCursorOrchestrator orchestrator,
        CharacterRosterService rosterService,
        CursorAgentLimits limits,
        ILogger<CharacterRosterPlugin> logger)
    {
        _generator = generator ?? throw new ArgumentNullException(nameof(generator));
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _rosterService = rosterService ?? throw new ArgumentNullException(nameof(rosterService));
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    [KernelFunction("generate_character_roster")]
    [Description("Fast scan of the document and build a compact character roster from detected name mentions.")]
    public async Task<CharacterRosterCommandResult> GenerateCharacterRosterAsync(
        CancellationToken cancellationToken = default)
    {
        var roster = await _orchestrator.BuildRosterAsync(cancellationToken);

        _logger.LogInformation("Character roster generated: {RosterId} v{Version}", roster.RosterId, roster.Version);
        return new CharacterRosterCommandResult(roster.RosterId, roster.Version);
    }

    [KernelFunction("generate_character_dossiers")]
    [Description("Scan the document and build a detailed character dossier catalog (slower, uses LLM).")]
    public async Task<CharacterRosterCommandResult> GenerateCharacterDossiersAsync(
        CancellationToken cancellationToken = default)
    {
        var roster = await _orchestrator.BuildRosterAsync(cancellationToken);

        _logger.LogInformation("Character roster generated: {RosterId} v{Version}", roster.RosterId, roster.Version);
        return new CharacterRosterCommandResult(roster.RosterId, roster.Version);
    }

    [KernelFunction("get_character_roster")]
    [Description("Return the current character roster (name, description, gender, aliases).")]
    public CharacterRosterPayload GetCharacterRoster()
    {
        var roster = _rosterService.GetRoster();
        var characters = roster.Characters
            .Select(c => new CharacterRosterEntry(
                c.CharacterId,
                c.Name,
                c.Description,
                c.Gender,
                c.Aliases))
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
            string.IsNullOrWhiteSpace(gender) ? "unknown" : gender.Trim());

        var saved = _rosterService.UpsertCharacter(profile);
        var roster = _rosterService.GetRoster();
        _logger.LogInformation("Character upserted: {CharacterId}", saved.CharacterId);

        return new CharacterRosterCommandResult(roster.RosterId, roster.Version, saved.CharacterId);
    }

    [KernelFunction("refresh_character_roster")]
    [Description("Refresh the character roster. Provide changed semantic pointers to re-index partially, or omit to rebuild fully.")]
    public async Task<CharacterRosterCommandResult> RefreshCharacterRosterAsync(
        string[]? changedPointers = null,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizePointers(changedPointers);
        if (normalized.Count == 0)
        {
            var roster = await _orchestrator.BuildRosterAsync(cancellationToken);
            _logger.LogInformation("Character roster generated: {RosterId} v{Version}", roster.RosterId, roster.Version);
            return new CharacterRosterCommandResult(roster.RosterId, roster.Version);
        }

        if (normalized.Count > _limits.MaxElements)
        {
            _logger.LogInformation("RefreshCharacterRoster: too many pointers ({Count}), running full generation.", normalized.Count);
            var roster = await _orchestrator.BuildRosterAsync(cancellationToken);
            _logger.LogInformation("Character roster generated: {RosterId} v{Version}", roster.RosterId, roster.Version);
            return new CharacterRosterCommandResult(roster.RosterId, roster.Version);
        }

        CharacterRoster refreshed = await _generator.RefreshAsync(normalized, cancellationToken);
        return new CharacterRosterCommandResult(refreshed.RosterId, refreshed.Version);
    }

    [KernelFunction("refresh_character_dossiers")]
    [Description("Refresh the character dossiers. Provide changed semantic pointers to re-index partially, or omit to rebuild fully.")]
    public async Task<CharacterRosterCommandResult> RefreshCharacterDossiersAsync(
        string[]? changedPointers = null,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizePointers(changedPointers);
        if (normalized.Count == 0)
        {
            var roster = await _orchestrator.BuildRosterAsync(cancellationToken);
            _logger.LogInformation("Character roster generated: {RosterId} v{Version}", roster.RosterId, roster.Version);
            return new CharacterRosterCommandResult(roster.RosterId, roster.Version);
        }

        if (normalized.Count > _limits.MaxElements)
        {
            _logger.LogInformation("RefreshCharacterRoster: too many pointers ({Count}), running full generation.", normalized.Count);
            var roster = await _orchestrator.BuildRosterAsync(cancellationToken);
            _logger.LogInformation("Character roster generated: {RosterId} v{Version}", roster.RosterId, roster.Version);
            return new CharacterRosterCommandResult(roster.RosterId, roster.Version);
        }

        CharacterRoster refreshed = await _generator.RefreshAsync(normalized, cancellationToken);
        return new CharacterRosterCommandResult(refreshed.RosterId, refreshed.Version);
    }

    private static List<string> NormalizePointers(string[]? changedPointers)
    {
        return changedPointers?
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList() ?? new List<string>();
    }

}
