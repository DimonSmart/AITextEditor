using System.ComponentModel;
using AiTextEditor.Core.Model;
using AiTextEditor.Core.Services;
using AiTextEditor.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiTextEditor.Agent;

public sealed class CharacterDossiersPlugin
{
    private readonly CharacterDossiersGenerator generator;
    private readonly ICursorStore cursorStore;
    private readonly CharacterDossierService dossierService;
    private readonly CursorAgentLimits limits;
    private readonly ILogger<CharacterDossiersPlugin> logger;
    private readonly CharacterBibleWorkflowRunner workflowRunner;

    public CharacterDossiersPlugin(
        CharacterDossiersGenerator generator,
        ICursorStore cursorStore,
        CharacterDossierService dossierService,
        CursorAgentLimits limits,
        ILogger<CharacterDossiersPlugin> logger,
        CharacterBibleWorkflowRunner? workflowRunner = null)
    {
        this.generator = generator ?? throw new ArgumentNullException(nameof(generator));
        this.cursorStore = cursorStore ?? throw new ArgumentNullException(nameof(cursorStore));
        this.dossierService = dossierService ?? throw new ArgumentNullException(nameof(dossierService));
        this.limits = limits ?? throw new ArgumentNullException(nameof(limits));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.workflowRunner = workflowRunner ?? new CharacterBibleWorkflowRunner(this.generator, NullLoggerFactory.Instance);
    }

    [Description("Scan the document and build a detailed character dossier catalog.")]
    public async Task<CharacterDossiersCommandResult> GenerateCharacterDossiersAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await workflowRunner.RunAsync(cancellationToken: cancellationToken);
        var dossiers = result.Dossiers;

        logger.LogInformation("Character dossiers generated: {DossiersId} v{Version}", dossiers.DossiersId, dossiers.Version);
        return new CharacterDossiersCommandResult(dossiers.DossiersId, dossiers.Version, Status: "updated");
    }

    [Description("Scan a named cursor and update character dossiers from its current content.")]
    public async Task<CharacterDossiersCommandResult> UpdateCharacterDossiersFromCursorAsync(
        string cursorName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cursorName);
        cancellationToken.ThrowIfCancellationRequested();

        if (!cursorStore.TryGetCursor(cursorName, out var cursor) || cursor is null)
        {
            throw new InvalidOperationException($"cursor_not_found: {cursorName}");
        }

        var evidence = new List<EvidenceItem>();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var portion = cursor.NextPortion();
            if (portion.Items.Count == 0 && !portion.HasMore)
            {
                break;
            }

            foreach (var item in portion.Items)
            {
                if (item.Type == LinearItemType.Heading)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(item.Markdown))
                {
                    continue;
                }

                evidence.Add(new EvidenceItem(item.Pointer.ToCompactString(), item.Markdown, Reason: null));
            }

            if (!portion.HasMore)
            {
                break;
            }
        }

        var dossiers = await generator.UpdateFromEvidenceBatchAsync(evidence, cancellationToken);
        logger.LogInformation("Character dossiers updated from cursor: {Cursor} -> {DossiersId} v{Version}", cursorName, dossiers.DossiersId, dossiers.Version);
        return new CharacterDossiersCommandResult(dossiers.DossiersId, dossiers.Version, Status: "updated");
    }

    [Description("Return the current character dossiers.")]
    public CharacterDossiersPayload GetCharacterDossiers()
    {
        var dossiers = dossierService.GetDossiers();
        var characters = dossiers.Characters
            .Select(c => new CharacterDossierEntry(
                c.CharacterId,
                c.Name,
                c.Gender,
                c.Description,
                c.Aliases,
                c.AliasExamples))
            .ToList();

        return new CharacterDossiersPayload(dossiers.DossiersId, dossiers.Version, characters);
    }

    [Description("Refresh the character dossiers. Provide changed semantic pointers to re-index partially, or omit to rebuild fully.")]
    public async Task<CharacterDossiersCommandResult> RefreshCharacterDossiersAsync(
        string[]? changedPointers = null,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizePointers(changedPointers);
        if (normalized.Count == 0)
        {
            var result = await workflowRunner.RunAsync(cancellationToken: cancellationToken);
            var dossiers = result.Dossiers;
            logger.LogInformation("Character dossiers generated: {DossiersId} v{Version}", dossiers.DossiersId, dossiers.Version);
            return new CharacterDossiersCommandResult(dossiers.DossiersId, dossiers.Version, Status: "updated");
        }

        if (normalized.Count > limits.MaxElements)
        {
            logger.LogInformation("RefreshCharacterDossiers: too many pointers ({Count}), running full generation.", normalized.Count);
            var result = await workflowRunner.RunAsync(cancellationToken: cancellationToken);
            var dossiers = result.Dossiers;
            logger.LogInformation("Character dossiers generated: {DossiersId} v{Version}", dossiers.DossiersId, dossiers.Version);
            return new CharacterDossiersCommandResult(dossiers.DossiersId, dossiers.Version, Status: "updated");
        }

        var refreshed = await workflowRunner.RunAsync(new CharacterBibleWorkflowRequest(normalized), cancellationToken);
        return new CharacterDossiersCommandResult(refreshed.Dossiers.DossiersId, refreshed.Dossiers.Version, Status: "updated");
    }

    [Description("Manually create/update a character dossier. If characterId is empty, resolves by name/aliases; ambiguous does not auto-merge.")]
    public CharacterDossiersCommandResult UpsertCharacterDossier(
        string name,
        string gender = "unknown",
        Dictionary<string, string>? aliasExamples = null,
        string? characterId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (!string.IsNullOrWhiteSpace(characterId))
        {
            var updated = dossierService.UpdateDossierById(
                characterId.Trim(),
                name: name.Trim(),
                gender: gender,
                aliasExamples: aliasExamples,
                facts: null,
                description: null);

            var dossiers = dossierService.GetDossiers();
            logger.LogInformation("Character dossier updated: {CharacterId}", updated.CharacterId);
            return new CharacterDossiersCommandResult(dossiers.DossiersId, dossiers.Version, Status: "updated", CharacterId: updated.CharacterId);
        }

        var result = dossierService.ResolveAndUpsertDossier(
            name.Trim(),
            gender,
            aliasExamples,
            facts: null,
            description: null);

        return new CharacterDossiersCommandResult(
            result.DossiersId,
            result.Version,
            Status: result.Status,
            CharacterId: result.CharacterId,
            CandidateIds: result.CandidateIds);
    }

    private static List<string> NormalizePointers(string[]? changedPointers)
    {
        return changedPointers?
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList() ?? [];
    }
}
