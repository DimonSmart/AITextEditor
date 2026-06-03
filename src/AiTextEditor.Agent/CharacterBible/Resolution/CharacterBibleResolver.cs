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
            (currentArchive, candidate, candidateIndex, token) => ResolveIdentityAsync(currentArchive, candidate, candidateIndex, progress, token),
            progress,
            cancellationToken);
    }

    private async Task<IdentityResolutionDecision> ResolveIdentityAsync(
        CharacterDossiers currentArchive,
        CharacterBibleCharacterCandidate candidate,
        int candidateIndex,
        IProgress<CharacterBibleWorkflowProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (currentArchive.Characters.Count == 0)
        {
            CharacterBibleRunLogScope.Current?.Info(
                "resolve.fast_path.empty_archive",
                $"candidateIndex={candidateIndex} name={LogValueFormatter.Quote(candidate.CanonicalName)} decision=new reason={LogValueFormatter.Quote("archive is empty")}");
            return IdentityResolutionDecision.New("Archive is empty; candidate cannot match an existing character.");
        }

        var searchTool = new CharacterArchiveSearchToolAdapter(
            currentArchive,
            characterVectorSearchTool,
            candidateIndex,
            candidate.CanonicalName);
        CharacterIdentityResolutionPromptInput promptInput;
        try
        {
            promptInput = identityResolutionPromptBuilder.BuildPromptInput(candidate);
        }
        catch (InvalidOperationException)
        {
            CharacterBibleRunLogScope.Current?.Error(
                "resolve.prompt.input.invalid",
                $"candidateIndex={candidateIndex} name={LogValueFormatter.Quote(candidate.CanonicalName)} candidatePointers={LogValueFormatter.List(candidate.Evidence.Select(evidence => evidence.Pointer))} materializedEvidencePointers={LogValueFormatter.List(candidate.Evidence.Where(evidence => !string.IsNullOrWhiteSpace(evidence.Excerpt)).Select(evidence => evidence.Pointer))}");
            throw;
        }

        CharacterBibleRunLogScope.Current?.Info(
            "resolve.prompt.input",
            $"candidateIndex={candidateIndex} name={LogValueFormatter.Quote(candidate.CanonicalName)} evidenceCount={promptInput.Candidate.Evidence.Count} evidencePointers={LogValueFormatter.List(promptInput.Candidate.Evidence.Select(evidence => evidence.Pointer))}");
        CharacterBibleLlmInputLogger.DebugInput(
            "resolve.llm.input",
            $"candidateIndex={candidateIndex} name={LogValueFormatter.Quote(candidate.CanonicalName)} modelType={nameof(CharacterIdentityResolutionPromptInput)}",
            promptInput);

        var response = await identityResolutionModelClient.ResolveAsync(
            new CharacterIdentityResolutionModelRequest(
                identityResolutionPromptBuilder.BuildSystemPrompt(),
                identityResolutionPromptBuilder.BuildUserPrompt(promptInput),
                searchTool,
                new CharacterBibleAgentDiagnosticProgress(
                    progress,
                    "resolve",
                    $"Identity resolver for {candidate.CanonicalName}",
                    $"candidateIndex={candidateIndex} name={LogValueFormatter.Quote(candidate.CanonicalName)}")),
            cancellationToken).ConfigureAwait(false);

        LogProtocolDiagnostics(candidate, candidateIndex, response, currentArchive, searchTool.ObservedCharacterIds);
        var decision = ToIdentityDecision(response, currentArchive);
        if (decision.Kind == IdentityResolutionKind.IdentityConflict)
        {
            decision = await ProposeSplitAsync(candidate, candidateIndex, decision, currentArchive, progress, cancellationToken)
                .ConfigureAwait(false);
        }

        return decision;
    }

    private async Task<IdentityResolutionDecision> ProposeSplitAsync(
        CharacterBibleCharacterCandidate candidate,
        int candidateIndex,
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
                candidateIndex,
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
                $"candidateIndex={candidateIndex} name={LogValueFormatter.Quote(candidate.CanonicalName)} characterIds={LogValueFormatter.List(decision.CharacterIds)}");
            var proposal = await splitCandidateModelClient.ProposeSplitAsync(
                new SplitCandidateModelRequest(
                    splitCandidatePromptBuilder.BuildSystemPrompt(),
                    splitCandidatePromptBuilder.BuildUserPrompt(candidate, decision, archiveSearchResult.Hits),
                    new CharacterBibleAgentDiagnosticProgress(
                        progress,
                        "split",
                        $"Split proposal for {candidate.CanonicalName}",
                        $"candidateIndex={candidateIndex} name={LogValueFormatter.Quote(candidate.CanonicalName)}")),
                cancellationToken).ConfigureAwait(false);
            progress?.Report(new CharacterBibleWorkflowProgress(
                "split",
                $"Split proposal for {candidate.CanonicalName}: {proposal.Kind}."));
            CharacterBibleRunLogScope.Current?.Info(
                "split.result",
                $"candidateIndex={candidateIndex} name={LogValueFormatter.Quote(candidate.CanonicalName)} kind={LogValueFormatter.Quote(proposal.Kind)} reason={LogValueFormatter.Quote(proposal.Reason)}");
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
                $"candidateIndex={candidateIndex} name={LogValueFormatter.Quote(candidate.CanonicalName)} message={LogValueFormatter.Quote(ex.Message)}",
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
            CharacterIdentityDecision.Existing => ResolveExisting(response.CharacterId, currentArchive, reason),
            CharacterIdentityDecision.New => IdentityResolutionDecision.New(reason),
            CharacterIdentityDecision.Ambiguous => IdentityResolutionDecision.Ambiguous(
                ResolveExistingCharacterIds(response.CharacterIds, currentArchive),
                reason),
            CharacterIdentityDecision.IdentityConflict => IdentityResolutionDecision.IdentityConflict(
                ResolveExistingCharacterIds(response.CharacterIds, currentArchive),
                reason),
            CharacterIdentityDecision.Defer => IdentityResolutionDecision.Defer(
                NormalizeCharacterIds(response.CharacterIds),
                reason),
            _ => IdentityResolutionDecision.Defer([], "Identity resolver agent returned unsupported decision.")
        };
    }

    private static void LogProtocolDiagnostics(
        CharacterBibleCharacterCandidate candidate,
        int candidateIndex,
        CharacterIdentityResolutionResponse response,
        CharacterDossiers currentArchive,
        IReadOnlySet<int> observedCharacterIds)
    {
        var logger = CharacterBibleRunLogScope.Current;
        if (logger is null)
        {
            return;
        }

        if (response.Decision == CharacterIdentityDecision.Existing)
        {
            if (response.CharacterId is null)
            {
                logger.Warning(
                    "resolve.protocol.error",
                    $"candidateIndex={candidateIndex} name={LogValueFormatter.Quote(candidate.CanonicalName)} decision=existing message={LogValueFormatter.Quote("characterId is missing")}");
                logger.Warning(
                    "resolve.protocol.defer",
                    $"candidateIndex={candidateIndex} name={LogValueFormatter.Quote(candidate.CanonicalName)} reason={LogValueFormatter.Quote("resolver returned missing characterId for existing decision")}");
                return;
            }

            var characterId = response.CharacterId.Value;
            if (!observedCharacterIds.Contains(characterId))
            {
                logger.Warning(
                    "resolve.protocol.warning",
                    $"candidateIndex={candidateIndex} name={LogValueFormatter.Quote(candidate.CanonicalName)} decision=existing characterId={characterId} message={LogValueFormatter.Quote("characterId was not present in observed search hits")}");
            }

            if (!currentArchive.Characters.Any(character => character.CharacterId == characterId))
            {
                logger.Warning(
                    "resolve.protocol.defer",
                    $"candidateIndex={candidateIndex} name={LogValueFormatter.Quote(candidate.CanonicalName)} reason={LogValueFormatter.Quote("resolver returned missing archive entry for existing decision")}");
            }
        }
    }

    private static IdentityResolutionDecision ResolveExisting(
        int? characterId,
        CharacterDossiers currentArchive,
        string reason)
    {
        if (characterId is null)
        {
            return IdentityResolutionDecision.Defer([], "Identity resolver did not return characterId for existing decision.");
        }

        var normalizedCharacterId = characterId.Value;
        if (!currentArchive.Characters.Any(character => character.CharacterId == normalizedCharacterId))
        {
            return IdentityResolutionDecision.Defer(
                [normalizedCharacterId],
                "Identity resolver targeted a missing archive character.");
        }

        return IdentityResolutionDecision.Existing(normalizedCharacterId, reason);
    }

    private static IReadOnlyList<int> ResolveExistingCharacterIds(
        IReadOnlyList<int>? characterIds,
        CharacterDossiers currentArchive)
    {
        return NormalizeCharacterIds(characterIds)
            .Where(characterId => currentArchive.Characters.Any(character => character.CharacterId == characterId))
            .ToArray();
    }

    private static IReadOnlyList<int> NormalizeCharacterIds(IReadOnlyList<int>? characterIds)
    {
        return characterIds?
            .Where(id => id > 0)
            .Distinct()
            .ToArray() ?? [];
    }

}
