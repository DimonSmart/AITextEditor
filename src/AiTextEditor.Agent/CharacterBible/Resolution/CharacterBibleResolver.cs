using AiTextEditor.Agent.CharacterBible.Patching;
using AiTextEditor.Agent.CharacterBible.Diagnostics;
using AiTextEditor.Agent.CharacterBible.VectorSearch;
using AiTextEditor.Core.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiTextEditor.Agent.CharacterBible.Resolution;

internal sealed class CharacterBibleResolver
{
    private readonly CharacterBibleCandidateResolutionApplier candidateResolutionApplier;
    private readonly ICharacterIdentityResolutionModelClient identityResolutionModelClient;
    private readonly ICharacterVectorSearchTool characterVectorSearchTool;
    private readonly CharacterIdentityResolutionPromptBuilder identityResolutionPromptBuilder;
    private readonly ISplitCandidateModelClient? splitCandidateModelClient;
    private readonly SplitCandidatePromptBuilder splitCandidatePromptBuilder;
    private readonly ILogger<CharacterBibleResolver> logger;

    public CharacterBibleResolver(
        CharacterBibleExtractionLimits limits,
        ICharacterIdentityResolutionModelClient identityResolutionModelClient,
        ICharacterVectorSearchTool characterVectorSearchTool,
        CharacterIdentityResolutionPromptBuilder? identityResolutionPromptBuilder = null,
        ISplitCandidateModelClient? splitCandidateModelClient = null,
        SplitCandidatePromptBuilder? splitCandidatePromptBuilder = null,
        ILogger<CharacterBibleResolver>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(limits);
        this.identityResolutionModelClient = identityResolutionModelClient ?? throw new ArgumentNullException(nameof(identityResolutionModelClient));
        this.characterVectorSearchTool = characterVectorSearchTool ?? throw new ArgumentNullException(nameof(characterVectorSearchTool));

        candidateResolutionApplier = new CharacterBibleCandidateResolutionApplier(limits);
        this.identityResolutionPromptBuilder = identityResolutionPromptBuilder ?? new CharacterIdentityResolutionPromptBuilder();
        this.splitCandidateModelClient = splitCandidateModelClient;
        this.splitCandidatePromptBuilder = splitCandidatePromptBuilder ?? new SplitCandidatePromptBuilder();
        this.logger = logger ?? NullLogger<CharacterBibleResolver>.Instance;
    }

    public Task<CharacterBibleRunState> ResolveAndUpdateCatalogAsync(
        CharacterBibleWorkflowInput request,
        CharacterDossierEditSession session,
        int paragraphCount,
        IReadOnlyList<CharacterBibleCharacterCandidate> candidates,
        IProgress<CharacterBibleWorkflowProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(candidates);

        return candidateResolutionApplier.ResolveAndUpdateCatalogAsync(
            request,
            session,
            paragraphCount,
            candidates,
            (currentArchive, candidate, token) => ResolveIdentityAsync(currentArchive, candidate, progress, token),
            progress,
            cancellationToken);
    }

    private async Task<IdentityResolutionDecision> ResolveIdentityAsync(
        CharacterDossiers currentArchive,
        CharacterBibleCharacterCandidate candidate,
        IProgress<CharacterBibleWorkflowProgress>? progress,
        CancellationToken cancellationToken)
    {
        var searchTool = new CharacterArchiveSearchToolAdapter(
            currentArchive,
            characterVectorSearchTool,
            candidate.CandidateId,
            candidate.CanonicalName);
        var response = await identityResolutionModelClient.ResolveAsync(
            new CharacterIdentityResolutionModelRequest(
                identityResolutionPromptBuilder.BuildSystemPrompt(),
                identityResolutionPromptBuilder.BuildUserPrompt(candidate),
                searchTool,
                new CharacterBibleAgentDiagnosticProgress(
                    progress,
                    "resolve",
                    $"Identity resolver for {candidate.CanonicalName}",
                    $"candidateId={LogValueFormatter.ShortId(candidate.CandidateId)} name={LogValueFormatter.Quote(candidate.CanonicalName)}")),
            cancellationToken).ConfigureAwait(false);

        LogProtocolDiagnostics(candidate, response, currentArchive, searchTool.ObservedEntryIds);
        var decision = ToIdentityDecision(response, currentArchive);
        if (decision.Kind == IdentityResolutionKind.IdentityConflict)
        {
            decision = await ProposeSplitAsync(candidate, decision, currentArchive, progress, cancellationToken)
                .ConfigureAwait(false);
        }

        return decision;
    }

    private async Task<IdentityResolutionDecision> ProposeSplitAsync(
        CharacterBibleCharacterCandidate candidate,
        IdentityResolutionDecision decision,
        CharacterDossiers currentArchive,
        IProgress<CharacterBibleWorkflowProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (splitCandidateModelClient is null)
        {
            return decision;
        }

        var archiveSearchResult = await new CharacterArchiveSearchToolAdapter(
                currentArchive,
                characterVectorSearchTool,
                candidate.CandidateId,
                candidate.CanonicalName)
            .SearchCharactersAsync(
                string.Join(' ', candidate.CanonicalName, string.Join(' ', candidate.AliasExamples.Keys)),
                limit: 10,
                cancellationToken)
            .ConfigureAwait(false);

        try
        {
            progress?.Report(new CharacterBibleWorkflowProgress(
                "split",
                $"Proposing split for identity conflict: {candidate.CanonicalName}."));
            CharacterBibleRunLogScope.Current?.Info(
                "split.start",
                $"candidateId={LogValueFormatter.ShortId(candidate.CandidateId)} name={LogValueFormatter.Quote(candidate.CanonicalName)} entryIds={LogValueFormatter.List(decision.AlternativeEntryIds)}");
            var proposal = await splitCandidateModelClient.ProposeSplitAsync(
                new SplitCandidateModelRequest(
                    splitCandidatePromptBuilder.BuildSystemPrompt(),
                    splitCandidatePromptBuilder.BuildUserPrompt(candidate, decision, archiveSearchResult.Hits),
                    new CharacterBibleAgentDiagnosticProgress(
                        progress,
                        "split",
                        $"Split proposal for {candidate.CanonicalName}",
                        $"candidateId={LogValueFormatter.ShortId(candidate.CandidateId)} name={LogValueFormatter.Quote(candidate.CanonicalName)}")),
                cancellationToken).ConfigureAwait(false);
            progress?.Report(new CharacterBibleWorkflowProgress(
                "split",
                $"Split proposal for {candidate.CanonicalName}: {proposal.Kind}."));
            CharacterBibleRunLogScope.Current?.Info(
                "split.result",
                $"candidateId={LogValueFormatter.ShortId(candidate.CandidateId)} name={LogValueFormatter.Quote(candidate.CanonicalName)} kind={LogValueFormatter.Quote(proposal.Kind)} reason={LogValueFormatter.Quote(proposal.Reason)}");
            return decision with { SplitProposal = proposal };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex,
                "split_candidate_agent_failed: candidate={CandidateName}",
                candidate.CanonicalName);
            CharacterBibleRunLogScope.Current?.Error(
                "split.failed",
                $"candidateId={LogValueFormatter.ShortId(candidate.CandidateId)} name={LogValueFormatter.Quote(candidate.CanonicalName)} message={LogValueFormatter.Quote(ex.Message)}",
                ex);
            progress?.Report(new CharacterBibleWorkflowProgress(
                "split",
                $"Split candidate agent failed for {candidate.CanonicalName}; conflict recorded without split proposal.",
                IsError: true));
            return decision;
        }
    }

    private static IdentityResolutionDecision ToIdentityDecision(
        CharacterIdentityResolutionResponse response,
        CharacterDossiers currentArchive)
    {
        var reason = string.IsNullOrWhiteSpace(response.Reason)
            ? "Identity resolver agent returned no reason."
            : response.Reason.Trim();

        return response.Decision switch
        {
            CharacterIdentityDecision.Existing => ResolveExisting(response.EntryId, currentArchive, reason),
            CharacterIdentityDecision.New => IdentityResolutionDecision.New(reason),
            CharacterIdentityDecision.Ambiguous => IdentityResolutionDecision.Ambiguous(
                ResolveExistingEntryIds(response.EntryIds, currentArchive),
                reason),
            CharacterIdentityDecision.IdentityConflict => IdentityResolutionDecision.IdentityConflict(
                ResolveExistingEntryIds(response.EntryIds, currentArchive),
                reason),
            CharacterIdentityDecision.Defer => IdentityResolutionDecision.Defer(
                NormalizeEntryIds(response.EntryIds),
                reason),
            _ => IdentityResolutionDecision.Defer([], "Identity resolver agent returned unsupported decision.")
        };
    }

    private static void LogProtocolDiagnostics(
        CharacterBibleCharacterCandidate candidate,
        CharacterIdentityResolutionResponse response,
        CharacterDossiers currentArchive,
        IReadOnlySet<string> observedEntryIds)
    {
        var logger = CharacterBibleRunLogScope.Current;
        if (logger is null)
        {
            return;
        }

        if (response.Decision == CharacterIdentityDecision.Existing)
        {
            if (string.IsNullOrWhiteSpace(response.EntryId))
            {
                logger.Warning(
                    "resolve.protocol.error",
                    $"candidateId={LogValueFormatter.ShortId(candidate.CandidateId)} decision=existing message={LogValueFormatter.Quote("entryId is missing")}");
                logger.Warning(
                    "resolve.protocol.defer",
                    $"candidateId={LogValueFormatter.ShortId(candidate.CandidateId)} reason={LogValueFormatter.Quote("resolver returned missing entryId for existing decision")}");
                return;
            }

            var entryId = response.EntryId.Trim();
            if (!observedEntryIds.Contains(entryId))
            {
                logger.Warning(
                    "resolve.protocol.warning",
                    $"candidateId={LogValueFormatter.ShortId(candidate.CandidateId)} decision=existing entryId={entryId} message={LogValueFormatter.Quote("entryId was not present in observed search hits")}");
            }

            if (!currentArchive.Characters.Any(character => string.Equals(character.CharacterId, entryId, StringComparison.Ordinal)))
            {
                logger.Warning(
                    "resolve.protocol.defer",
                    $"candidateId={LogValueFormatter.ShortId(candidate.CandidateId)} reason={LogValueFormatter.Quote("resolver returned missing archive entry for existing decision")}");
            }
        }
    }

    private static IdentityResolutionDecision ResolveExisting(
        string? entryId,
        CharacterDossiers currentArchive,
        string reason)
    {
        if (string.IsNullOrWhiteSpace(entryId))
        {
            return IdentityResolutionDecision.Defer([], "Identity resolver did not return entryId for existing decision.");
        }

        var normalizedEntryId = entryId.Trim();
        if (!currentArchive.Characters.Any(character => string.Equals(character.CharacterId, normalizedEntryId, StringComparison.Ordinal)))
        {
            return IdentityResolutionDecision.Defer(
                [normalizedEntryId],
                "Identity resolver targeted a missing archive entry.");
        }

        return IdentityResolutionDecision.Existing(normalizedEntryId, reason);
    }

    private static IReadOnlyList<string> ResolveExistingEntryIds(
        IReadOnlyList<string>? entryIds,
        CharacterDossiers currentArchive)
    {
        return NormalizeEntryIds(entryIds)
            .Where(entryId => currentArchive.Characters.Any(character => string.Equals(character.CharacterId, entryId, StringComparison.Ordinal)))
            .ToArray();
    }

    private static IReadOnlyList<string> NormalizeEntryIds(IReadOnlyList<string>? entryIds)
    {
        return entryIds?
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray() ?? [];
    }

}
