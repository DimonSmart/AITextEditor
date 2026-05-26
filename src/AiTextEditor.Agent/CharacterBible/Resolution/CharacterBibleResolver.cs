using AiTextEditor.Core.Services;
using AiTextEditor.Core.Model;
using AiTextEditor.Agent.CharacterBible.Patching;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiTextEditor.Agent.CharacterBible.Resolution;

internal sealed class CharacterBibleResolver
{
    private const int ArchiveSearchMaxResults = int.MaxValue;

    private readonly CharacterDossierService dossierService;
    private readonly CharacterArchiveSearchService archiveSearchService;
    private readonly DeterministicIdentityResolution identityResolution;
    private readonly CharacterBibleCommitPlanBuilder commitPlanBuilder;
    private readonly ISuspectArchiveResolverModelClient? suspectArchiveResolverModelClient;
    private readonly SuspectArchiveResolverPromptBuilder suspectArchiveResolverPromptBuilder;
    private readonly ISplitCandidateModelClient? splitCandidateModelClient;
    private readonly SplitCandidatePromptBuilder splitCandidatePromptBuilder;
    private readonly ILogger<CharacterBibleResolver> logger;

    public CharacterBibleResolver(
        CharacterDossierService dossierService,
        CharacterBibleExtractionLimits limits,
        ISuspectArchiveResolverModelClient? suspectArchiveResolverModelClient = null,
        SuspectArchiveResolverPromptBuilder? suspectArchiveResolverPromptBuilder = null,
        ISplitCandidateModelClient? splitCandidateModelClient = null,
        SplitCandidatePromptBuilder? splitCandidatePromptBuilder = null,
        ILogger<CharacterBibleResolver>? logger = null)
    {
        this.dossierService = dossierService ?? throw new ArgumentNullException(nameof(dossierService));
        ArgumentNullException.ThrowIfNull(limits);

        archiveSearchService = new CharacterArchiveSearchService();
        identityResolution = new DeterministicIdentityResolution();
        commitPlanBuilder = new CharacterBibleCommitPlanBuilder(limits);
        this.suspectArchiveResolverModelClient = suspectArchiveResolverModelClient;
        this.suspectArchiveResolverPromptBuilder = suspectArchiveResolverPromptBuilder ?? new SuspectArchiveResolverPromptBuilder();
        this.splitCandidateModelClient = splitCandidateModelClient;
        this.splitCandidatePromptBuilder = splitCandidatePromptBuilder ?? new SplitCandidatePromptBuilder();
        this.logger = logger ?? NullLogger<CharacterBibleResolver>.Instance;
    }

    public Task<CharacterBibleCommitPlan> CreateCommitPlanAsync(
        CharacterBibleWorkflowInput request,
        int paragraphCount,
        IReadOnlyList<CharacterBibleCharacterCandidate> candidates,
        IProgress<CharacterBibleWorkflowProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(candidates);

        var baseDossiers = dossierService.GetDossiers();

        return commitPlanBuilder.BuildAsync(
            request,
            baseDossiers,
            paragraphCount,
            candidates,
            (currentArchive, candidate, token) => ResolveIdentityAsync(currentArchive, candidate, progress, token),
            progress,
            cancellationToken);
    }

    private async Task<IdentityResolutionDecision> ResolveIdentityAsync(
        CharacterDossiers baseDossiers,
        CharacterBibleCharacterCandidate candidate,
        IProgress<CharacterBibleWorkflowProgress>? progress,
        CancellationToken cancellationToken)
    {
        var archiveHits = archiveSearchService.Search(
            baseDossiers,
            CharacterArchiveSearchService.CreateRequest(candidate, ArchiveSearchMaxResults));
        if (suspectArchiveResolverModelClient is null)
        {
            return identityResolution.Resolve(archiveHits);
        }

        try
        {
            var response = await suspectArchiveResolverModelClient.ResolveAsync(
                new SuspectArchiveResolverModelRequest(
                    suspectArchiveResolverPromptBuilder.BuildSystemPrompt(),
                    suspectArchiveResolverPromptBuilder.BuildUserPrompt(candidate, archiveHits)),
                cancellationToken).ConfigureAwait(false);

            var decision = ToIdentityDecision(response, archiveHits);
            if (decision.Kind == IdentityResolutionKind.IdentityConflict)
            {
                decision = await ProposeSplitAsync(candidate, decision, archiveHits, progress, cancellationToken)
                    .ConfigureAwait(false);
            }

            return decision;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex,
                "suspect_archive_resolver_failed: candidate={CandidateName}",
                candidate.CanonicalName);
            progress?.Report(new CharacterBibleWorkflowProgress(
                "resolve",
                $"Identity resolver agent failed for {candidate.CanonicalName}; using deterministic fallback."));
            return identityResolution.Resolve(archiveHits);
        }
    }

    private async Task<IdentityResolutionDecision> ProposeSplitAsync(
        CharacterBibleCharacterCandidate candidate,
        IdentityResolutionDecision decision,
        IReadOnlyList<CharacterArchiveHit> archiveHits,
        IProgress<CharacterBibleWorkflowProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (splitCandidateModelClient is null)
        {
            return decision;
        }

        try
        {
            progress?.Report(new CharacterBibleWorkflowProgress(
                "split",
                $"Proposing split for identity conflict: {candidate.CanonicalName}."));
            var proposal = await splitCandidateModelClient.ProposeSplitAsync(
                new SplitCandidateModelRequest(
                    splitCandidatePromptBuilder.BuildSystemPrompt(),
                    splitCandidatePromptBuilder.BuildUserPrompt(candidate, decision, archiveHits)),
                cancellationToken).ConfigureAwait(false);
            progress?.Report(new CharacterBibleWorkflowProgress(
                "split",
                $"Split proposal for {candidate.CanonicalName}: {proposal.Kind}."));
            return decision with { SplitProposal = proposal };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex,
                "split_candidate_agent_failed: candidate={CandidateName}",
                candidate.CanonicalName);
            progress?.Report(new CharacterBibleWorkflowProgress(
                "split",
                $"Split candidate agent failed for {candidate.CanonicalName}; conflict recorded without split proposal."));
            return decision;
        }
    }

    private IdentityResolutionDecision ToIdentityDecision(
        SuspectArchiveResolverResponse response,
        IReadOnlyList<CharacterArchiveHit> archiveHits)
    {
        var alternatives = NormalizeEntryIds(response.AlternativeEntryIds);
        var reason = string.IsNullOrWhiteSpace(response.Reason)
            ? "Identity resolver agent returned no reason."
            : response.Reason.Trim();

        return response.Kind switch
        {
            "existing" => ResolveExisting(response.TargetEntryId, archiveHits, reason),
            "new" => IdentityResolutionDecision.New(reason),
            "ambiguous" => IdentityResolutionDecision.Ambiguous(alternatives, reason),
            "defer" => IdentityResolutionDecision.Defer(alternatives, reason),
            "identity_conflict" => IdentityResolutionDecision.IdentityConflict(alternatives, reason),
            _ => identityResolution.Resolve(archiveHits)
        };
    }

    private IdentityResolutionDecision ResolveExisting(
        string? targetEntryId,
        IReadOnlyList<CharacterArchiveHit> archiveHits,
        string reason)
    {
        if (string.IsNullOrWhiteSpace(targetEntryId))
        {
            return identityResolution.Resolve(archiveHits);
        }

        var hit = archiveHits.FirstOrDefault(item => string.Equals(item.EntryId, targetEntryId, StringComparison.Ordinal));
        if (hit is null || hit.EntryKind != CharacterArchiveEntryKind.Character)
        {
            return IdentityResolutionDecision.Defer(
                [targetEntryId.Trim()],
                "Identity resolver targeted a non-character or missing archive entry.");
        }

        return IdentityResolutionDecision.Existing(hit.EntryId, hit, reason);
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
