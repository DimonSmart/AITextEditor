using AiTextEditor.Core.Model;
using AiTextEditor.Agent.CharacterBible.Diagnostics;
using Microsoft.Extensions.Logging;
using System.Text;

namespace AiTextEditor.Agent.CharacterBible.Patching;

internal sealed class CharacterBibleDossierPatcher
{
    private readonly IDossierPatchProposalModelClient modelClient;
    private readonly DossierPatchPromptBuilder promptBuilder;
    private readonly IDossierConsistencyReviewerModelClient reviewerModelClient;
    private readonly DossierConsistencyReviewerPromptBuilder reviewerPromptBuilder;
    private readonly CharacterBibleEvidenceContextExpander evidenceContextExpander;
    private readonly CharacterBibleDossierPatchLimits patchLimits;
    private readonly ILogger<CharacterBibleDossierPatcher> logger;

    public CharacterBibleDossierPatcher(
        IDossierPatchProposalModelClient modelClient,
        DossierPatchPromptBuilder promptBuilder,
        IDossierConsistencyReviewerModelClient reviewerModelClient,
        DossierConsistencyReviewerPromptBuilder reviewerPromptBuilder,
        CharacterBibleEvidenceContextExpander evidenceContextExpander,
        CharacterBibleDossierPatchLimits? patchLimits,
        ILogger<CharacterBibleDossierPatcher> logger)
    {
        this.modelClient = modelClient ?? throw new ArgumentNullException(nameof(modelClient));
        this.promptBuilder = promptBuilder ?? throw new ArgumentNullException(nameof(promptBuilder));
        this.reviewerModelClient = reviewerModelClient ?? throw new ArgumentNullException(nameof(reviewerModelClient));
        this.reviewerPromptBuilder = reviewerPromptBuilder ?? throw new ArgumentNullException(nameof(reviewerPromptBuilder));
        this.evidenceContextExpander = evidenceContextExpander ?? throw new ArgumentNullException(nameof(evidenceContextExpander));
        this.patchLimits = patchLimits ?? new CharacterBibleDossierPatchLimits();
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<CharacterBibleRunState> ApplyDossierPatchesAsync(
        CharacterBibleRunState runState,
        IProgress<CharacterBibleWorkflowProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runState);
        cancellationToken.ThrowIfCancellationRequested();

        if (runState.Failure is not null || runState.Candidates.Count == 0 || runState.Catalog.Decisions.Count == 0)
        {
            return runState;
        }

        var dossiersById = runState.Catalog.Current.Characters.ToDictionary(
            dossier => dossier.CharacterId,
            StringComparer.Ordinal);
        var proposalCount = 0;
        var appliedCount = 0;
        var patchGroups = BuildPatchGroups(runState);

        foreach (var patchGroup in patchGroups)
        {
            if (!dossiersById.TryGetValue(patchGroup.CharacterId, out var dossier))
            {
                CharacterBibleRunLogScope.Current?.Warning(
                    "patch.group.skipped",
                    $"characterId={patchGroup.CharacterId} reason={LogValueFormatter.Quote("character not found")}");
                continue;
            }

            proposalCount++;
            CharacterBibleRunLogScope.Current?.Info(
                "patch.group.start",
                $"characterId={dossier.CharacterId} name={LogValueFormatter.Quote(dossier.Name)} candidateCount={patchGroup.Candidates.Count} candidateIds={LogValueFormatter.List(patchGroup.Candidates.Select(candidate => candidate.Candidate.CandidateId))}");
            progress?.Report(new CharacterBibleWorkflowProgress(
                "patch",
                $"Proposing dossier patch for {dossier.Name} from {patchGroup.Candidates.Count} candidate evidence group(s)."));

            DossierPatchProposal proposal;
            try
            {
                var promptInput = DossierPatchPromptBuilder.BuildPromptInput(patchGroup.Candidates, dossier);
                CharacterBibleRunLogScope.Current?.Info(
                    "patch.proposal.call",
                    $"characterId={dossier.CharacterId} inputCandidates={patchGroup.Candidates.Count} evidencePointers={LogValueFormatter.List(patchGroup.Candidates.SelectMany(candidate => candidate.Candidate.Evidence).Select(evidence => evidence.Pointer))}");
                CharacterBibleLlmInputLogger.DebugInput(
                    "patch.proposal.llm.input",
                    $"characterId={dossier.CharacterId} candidateIds={LogValueFormatter.List(patchGroup.Candidates.Select(candidate => candidate.Candidate.CandidateId))} modelKeys={LogValueFormatter.List(["target", "currentProfile", "newEvidence"])} modelType={nameof(CharacterBiblePatchProposalPromptInput)}",
                    promptInput);
                CharacterBibleLlmInputLogger.DebugPatchProposalContract(promptInput);
                proposal = await modelClient.ProposePatchAsync(
                    new DossierPatchProposalModelRequest(
                        promptBuilder.BuildSystemPrompt(),
                        promptBuilder.BuildUserPrompt(promptInput),
                        new CharacterBibleAgentDiagnosticProgress(
                            progress,
                            "patch",
                            $"Patch proposal for {dossier.Name}",
                            $"characterId={dossier.CharacterId} name={LogValueFormatter.Quote(dossier.Name)}")),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                CharacterBibleRunLogScope.Current?.Error(
                    "patch.proposal.failed",
                    $"characterId={dossier.CharacterId} name={LogValueFormatter.Quote(dossier.Name)} message={LogValueFormatter.Quote(ex.Message)}",
                    ex);
                logger.LogError(ex, "Dossier patch proposal failed for character {CharacterId}. Profile unchanged.", dossier.CharacterId);
                progress?.Report(new CharacterBibleWorkflowProgress(
                    "patch",
                    $"Patch proposal failed for {dossier.Name}; profile unchanged.",
                    IsError: true));
                continue;
            }

            CharacterBibleRunLogScope.Current?.Info(
                "patch.proposal.result",
                $"characterId={dossier.CharacterId} status={LogValueFormatter.Quote(proposal.Status?.ToString())} additions={proposal.Additions?.Count ?? 0} changedFields={LogValueFormatter.List(GetChangedProfileFields(proposal.Additions))}");
            var newEvidence = DossierPatchPromptBuilder.BuildEvidence(patchGroup.Candidates);
            var validationIssues = ValidateProposal(proposal, newEvidence);
            if (validationIssues.Count > 0)
            {
                CharacterBibleRunLogScope.Current?.Warning(
                    "patch.proposal.invalid",
                    $"characterId={dossier.CharacterId} issues={LogValueFormatter.List(validationIssues)}");
                progress?.Report(new CharacterBibleWorkflowProgress(
                    "patch",
                    $"Patch proposal for {dossier.Name}: invalid contract details."));
                continue;
            }

            if (proposal.Status != CharacterBiblePatchProposalStatus.Ready)
            {
                CharacterBibleRunLogScope.Current?.Info(
                    "patch.apply.skipped",
                    $"characterId={dossier.CharacterId} reason={LogValueFormatter.Quote($"proposal status {proposal.Status}")}");
                progress?.Report(new CharacterBibleWorkflowProgress(
                    "patch",
                    $"Patch proposal for {dossier.Name}: {proposal.Status}."));
                continue;
            }

            progress?.Report(new CharacterBibleWorkflowProgress(
                "patch",
                $"Patch proposal for {dossier.Name}: ready. Reviewing patch."));
            var review = await ReviewPatchAsync(dossier, patchGroup.Candidates, proposal, progress, cancellationToken);
            CharacterBibleRunLogScope.Current?.Info(
                "patch.review.result",
                $"characterId={dossier.CharacterId} name={LogValueFormatter.Quote(dossier.Name)} verdict={LogValueFormatter.Quote(review.Verdict?.ToString())} issues={LogValueFormatter.List(FormatReviewIssues(review.Issues ?? []))}");
            if (review.Verdict != CharacterBiblePatchReviewVerdict.Approved)
            {
                CharacterBibleRunLogScope.Current?.Info(
                    "patch.apply.skipped",
                    $"characterId={dossier.CharacterId} reason={LogValueFormatter.Quote($"review verdict {review.Verdict}")}");
                progress?.Report(new CharacterBibleWorkflowProgress(
                    "patch",
                    $"Patch review for {dossier.Name}: {review.Verdict}."));
                continue;
            }

            var mergedProfile = MergeProfileAdditions(dossier.Profile, proposal.Additions ?? []);
            var profileChanged = !CharacterProfile.HasSameContent(dossier.Profile, mergedProfile);
            if (!profileChanged)
            {
                CharacterBibleRunLogScope.Current?.Info(
                    "patch.apply.no_change",
                    $"characterId={dossier.CharacterId} name={LogValueFormatter.Quote(dossier.Name)}");
                progress?.Report(new CharacterBibleWorkflowProgress(
                    "patch",
                    $"Patch review for {dossier.Name}: approved; no local changes after merge."));
                continue;
            }

            runState.Catalog.UpdateProfile(dossier.CharacterId, mergedProfile);

            var patchedDossier = runState.Catalog.GetRequired(dossier.CharacterId);
            dossiersById[dossier.CharacterId] = patchedDossier;
            appliedCount++;
            CharacterBibleRunLogScope.Current?.Info(
                "patch.apply",
                $"characterId={dossier.CharacterId} name={LogValueFormatter.Quote(dossier.Name)} profileFieldsUpdated={LogValueFormatter.List(GetChangedProfileFields(proposal.Additions))}");
            progress?.Report(new CharacterBibleWorkflowProgress(
                "patch",
                $"Patch applied for {dossier.Name}."));
        }

        if (proposalCount > 0)
        {
            progress?.Report(new CharacterBibleWorkflowProgress(
                "patch",
                $"Dossier patching finished: {appliedCount}/{proposalCount} patches applied."));
        }

        return runState;
    }

    private async Task<DossierReviewResult> ReviewPatchAsync(
        CharacterDossier dossier,
        IReadOnlyList<CharacterBibleDossierPatchCandidate> candidates,
        DossierPatchProposal proposal,
        IProgress<CharacterBibleWorkflowProgress>? progress,
        CancellationToken cancellationToken)
    {
        var evidence = DossierPatchPromptBuilder.BuildReferencedEvidence(candidates, proposal);

        try
        {
            var promptInput = DossierConsistencyReviewerPromptBuilder.BuildPromptInput(dossier, proposal, evidence);
            CharacterBibleLlmInputLogger.DebugInput(
                "patch.review.llm.input",
                $"characterId={dossier.CharacterId} candidateIds={LogValueFormatter.List(candidates.Select(candidate => candidate.Candidate.CandidateId))} referencedEvidenceCount={evidence.Count} modelType={nameof(DossierReviewPromptInput)}",
                promptInput);
            return await reviewerModelClient.ReviewAsync(
                new DossierReviewModelRequest(
                    reviewerPromptBuilder.BuildSystemPrompt(),
                    reviewerPromptBuilder.BuildUserPrompt(promptInput),
                    new CharacterBibleAgentDiagnosticProgress(
                        progress,
                        "patch",
                        $"Patch review for {dossier.Name}",
                        $"characterId={dossier.CharacterId} name={LogValueFormatter.Quote(dossier.Name)}")),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            CharacterBibleRunLogScope.Current?.Error(
                "patch.review.failed",
                $"characterId={dossier.CharacterId} name={LogValueFormatter.Quote(dossier.Name)} message={LogValueFormatter.Quote(ex.Message)}",
                ex);
            logger.LogError(ex, "Dossier patch review failed for character {CharacterId}. Patch rejected.", dossier.CharacterId);
            return new DossierReviewResult
            {
                Verdict = CharacterBiblePatchReviewVerdict.RevisePatch,
                Issues =
                [
                    new CharacterBiblePatchReviewIssue(
                        CharacterBiblePatchReviewIssueCode.UnsupportedClaim,
                        null,
                        "Reviewer call failed.")
                ]
            };
        }
    }

    private IReadOnlyList<DossierPatchGroup> BuildPatchGroups(CharacterBibleRunState runState)
    {
        var groups = new List<DossierPatchGroupBuilder>();
        var groupsByCharacterId = new Dictionary<string, DossierPatchGroupBuilder>(StringComparer.Ordinal);

        for (var index = 0; index < runState.Catalog.Decisions.Count && index < runState.Candidates.Count; index++)
        {
            var decision = runState.Catalog.Decisions[index];
            if (decision.Kind is not CharacterBibleDecisionKind.Existing and not CharacterBibleDecisionKind.New
                || string.IsNullOrWhiteSpace(decision.CharacterId))
            {
                continue;
            }

            var characterId = decision.CharacterId.Trim();
            if (!groupsByCharacterId.TryGetValue(characterId, out var group))
            {
                group = new DossierPatchGroupBuilder(characterId);
                groupsByCharacterId[characterId] = group;
                groups.Add(group);
            }

            var candidate = runState.Candidates[index];
            group.Candidates.Add(new CharacterBibleDossierPatchCandidate(
                candidate,
                evidenceContextExpander.Expand(candidate.Evidence)));
        }

        return groups
            .Where(group => group.Candidates.Count > 0)
            .SelectMany(SplitPatchGroup)
            .ToArray();
    }

    private IEnumerable<DossierPatchGroup> SplitPatchGroup(DossierPatchGroupBuilder group)
    {
        var maxCandidates = Math.Max(1, patchLimits.MaxCandidatesPerPatchCall);
        var maxBytes = Math.Max(1, patchLimits.MaxContextBytesPerPatchCall);
        var batch = new List<CharacterBibleDossierPatchCandidate>();
        var batchBytes = 0;

        foreach (var candidate in group.Candidates)
        {
            var candidateBytes = EstimatePatchCandidateBytes(candidate);
            var wouldOverflow = batch.Count >= maxCandidates || batchBytes + candidateBytes > maxBytes;
            if (wouldOverflow && batch.Count > 0)
            {
                yield return new DossierPatchGroup(group.CharacterId, batch.ToArray());
                batch = [];
                batchBytes = 0;
            }

            batch.Add(candidate);
            batchBytes += candidateBytes;
        }

        if (batch.Count > 0)
        {
            yield return new DossierPatchGroup(group.CharacterId, batch.ToArray());
        }
    }

    private static int EstimatePatchCandidateBytes(CharacterBibleDossierPatchCandidate patchCandidate)
    {
        var candidate = patchCandidate.Candidate;
        var bytes = GetUtf8ByteCount(candidate.CandidateId)
                    + GetUtf8ByteCount(candidate.CanonicalName)
                    + GetUtf8ByteCount(candidate.Gender);

        foreach (var alias in candidate.AliasExamples)
        {
            bytes += GetUtf8ByteCount(alias.Key) + GetUtf8ByteCount(alias.Value);
        }

        foreach (var evidence in candidate.Evidence)
        {
            bytes += GetUtf8ByteCount(evidence.Pointer) + GetUtf8ByteCount(evidence.Excerpt);
        }

        foreach (var context in patchCandidate.EvidenceContexts)
        {
            bytes += GetUtf8ByteCount(context.Pointer)
                     + GetUtf8ByteCount(context.AnchorExcerpt)
                     + GetUtf8ByteCount(context.CurrentParagraph)
                     + GetUtf8ByteCount(context.FocusedText);

            foreach (var nearby in context.NearbyParagraphs)
            {
                bytes += GetUtf8ByteCount(nearby.Pointer)
                         + GetUtf8ByteCount(nearby.Text)
                         + GetUtf8ByteCount(nearby.Position);
            }
        }

        return bytes;
    }

    private static int GetUtf8ByteCount(string? value)
        => string.IsNullOrEmpty(value) ? 0 : Encoding.UTF8.GetByteCount(value);

    private static IReadOnlyList<string> GetChangedProfileFields(IReadOnlyList<CharacterBibleProfileAddition>? additions)
    {
        if (additions is null || additions.Count == 0)
        {
            return [];
        }

        return additions
            .Where(addition => addition.Field is not null)
            .Select(addition => ToProfileFieldName(addition.Field!.Value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static CharacterProfile MergeProfileAdditions(
        CharacterProfile? existing,
        IReadOnlyList<CharacterBibleProfileAddition> additions)
    {
        var normalizedExisting = CharacterProfile.Normalize(existing);
        var appearance = normalizedExisting.Appearance;
        var statusAndCompetence = normalizedExisting.StatusAndCompetence;
        var psychologicalProfile = normalizedExisting.PsychologicalProfile;
        var speechAndCommunication = normalizedExisting.SpeechAndCommunication;

        foreach (var addition in additions)
        {
            var text = NullIfWhiteSpace(addition.Text);
            if (text is null || addition.Field is null)
            {
                continue;
            }

            switch (addition.Field.Value)
            {
                case CharacterBibleProfileField.Appearance:
                    appearance = MergeProfileField(appearance, text);
                    break;
                case CharacterBibleProfileField.StatusAndCompetence:
                    statusAndCompetence = MergeProfileField(statusAndCompetence, text);
                    break;
                case CharacterBibleProfileField.PsychologicalProfile:
                    psychologicalProfile = MergeProfileField(psychologicalProfile, text);
                    break;
                case CharacterBibleProfileField.SpeechAndCommunication:
                    speechAndCommunication = MergeProfileField(speechAndCommunication, text);
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported profile field '{addition.Field}'.");
            }
        }

        return CharacterProfile.Normalize(new CharacterProfile(
            appearance,
            statusAndCompetence,
            psychologicalProfile,
            speechAndCommunication));
    }

    private static string MergeProfileField(string existing, string? addition)
    {
        var normalizedAddition = NullIfWhiteSpace(addition);
        if (normalizedAddition is null)
        {
            return existing;
        }

        if (string.IsNullOrWhiteSpace(existing))
        {
            return normalizedAddition;
        }

        if (ContainsProfileText(existing, normalizedAddition))
        {
            return existing;
        }

        if (ContainsProfileText(normalizedAddition, existing))
        {
            return normalizedAddition;
        }

        var separator = EndsWithSentencePunctuation(existing) ? " " : "; ";
        return existing + separator + normalizedAddition;
    }

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool ContainsProfileText(string text, string fragment)
        => text.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0;

    private static bool EndsWithSentencePunctuation(string value)
        => value.Length > 0 && value[^1] is '.' or '!' or '?';

    private static IReadOnlyList<string> ValidateProposal(
        DossierPatchProposal proposal,
        IReadOnlyList<CharacterBiblePatchEvidence> newEvidence)
    {
        var issues = new List<string>();
        var allowedPointers = newEvidence
            .Select(evidence => evidence.Pointer)
            .ToHashSet(StringComparer.Ordinal);

        if (proposal.Status == CharacterBiblePatchProposalStatus.Ready && proposal.Additions?.Count == 0)
        {
            issues.Add("ready proposal has no additions");
        }

        if (proposal.Status == CharacterBiblePatchProposalStatus.NoUsefulChanges && proposal.Additions?.Count > 0)
        {
            issues.Add("noUsefulChanges proposal has additions");
        }

        foreach (var addition in proposal.Additions ?? [])
        {
            if (addition.EvidencePointers is null || addition.EvidencePointers.Count == 0)
            {
                issues.Add("addition is missing evidence pointers");
                continue;
            }

            foreach (var pointer in addition.EvidencePointers)
            {
                if (!allowedPointers.Contains(pointer))
                {
                    issues.Add($"evidence pointer not found: {pointer}");
                }
            }
        }

        return issues;
    }

    private static IReadOnlyList<string> FormatReviewIssues(IReadOnlyList<CharacterBiblePatchReviewIssue> issues)
    {
        return issues
            .Select(issue => $"{issue.Code}:{issue.Field}:{issue.Message}")
            .ToArray();
    }

    private static string ToProfileFieldName(CharacterBibleProfileField field)
        => field switch
        {
            CharacterBibleProfileField.Appearance => "appearance",
            CharacterBibleProfileField.StatusAndCompetence => "statusAndCompetence",
            CharacterBibleProfileField.PsychologicalProfile => "psychologicalProfile",
            CharacterBibleProfileField.SpeechAndCommunication => "speechAndCommunication",
            _ => throw new InvalidOperationException($"Unsupported profile field '{field}'.")
        };

    private sealed record DossierPatchGroup(
        string CharacterId,
        IReadOnlyList<CharacterBibleDossierPatchCandidate> Candidates);

    private sealed class DossierPatchGroupBuilder(string characterId)
    {
        public string CharacterId { get; } = characterId;

        public List<CharacterBibleDossierPatchCandidate> Candidates { get; } = [];
    }
}
