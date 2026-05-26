using AiTextEditor.Core.Model;
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

    public async Task<CharacterBibleCommitPlan> ApplyDossierPatchesAsync(
        CharacterBibleCommitPlan plan,
        IProgress<CharacterBibleWorkflowProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        cancellationToken.ThrowIfCancellationRequested();

        if (plan.Failure is not null || plan.Candidates.Count == 0 || plan.Decisions.Count == 0)
        {
            return plan;
        }

        var dossiersById = plan.ProjectedDossiers.Characters.ToDictionary(
            dossier => dossier.CharacterId,
            StringComparer.Ordinal);
        var changedDossiers = plan.ProjectedDossiers.Characters.ToList();
        var changed = plan.Changed;
        var proposalCount = 0;
        var appliedCount = 0;
        var patchGroups = BuildPatchGroups(plan);

        foreach (var patchGroup in patchGroups)
        {
            if (!dossiersById.TryGetValue(patchGroup.CharacterId, out var dossier))
            {
                continue;
            }

            proposalCount++;
            progress?.Report(new CharacterBibleWorkflowProgress(
                "patch",
                $"Proposing dossier patch for {dossier.Name} from {patchGroup.Candidates.Count} candidate evidence group(s)."));

            DossierPatchProposal proposal;
            try
            {
                proposal = await modelClient.ProposePatchAsync(
                    new DossierPatchProposalModelRequest(
                        promptBuilder.BuildSystemPrompt(),
                        promptBuilder.BuildUserPrompt(patchGroup.Candidates, patchGroup.Decision, dossier)),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Dossier patch proposal failed for character {CharacterId}. Profile unchanged.", dossier.CharacterId);
                progress?.Report(new CharacterBibleWorkflowProgress(
                    "patch",
                    $"Patch proposal failed for {dossier.Name}; profile unchanged."));
                continue;
            }

            if (!string.Equals(proposal.Status, "ready", StringComparison.Ordinal))
            {
                progress?.Report(new CharacterBibleWorkflowProgress(
                    "patch",
                    $"Patch proposal for {dossier.Name}: {proposal.Status}."));
                continue;
            }

            var review = await ReviewPatchAsync(dossier, patchGroup.Candidates, proposal, cancellationToken);
            if (!string.Equals(review.Verdict, "approved", StringComparison.Ordinal))
            {
                progress?.Report(new CharacterBibleWorkflowProgress(
                    "patch",
                    $"Patch review for {dossier.Name}: {review.Verdict}."));
                continue;
            }

            var aliasExamples = MergeAliases(dossier.AliasExamples, patchGroup.Candidates, proposal.AliasesToAdd);
            var aliasesChanged = !HasSameAliasExamples(dossier.AliasExamples, aliasExamples);
            var mergedProfile = MergeProfileAdditions(dossier.Profile, proposal.ProfilePatch);
            var profileChanged = !CharacterProfile.HasSameContent(dossier.Profile, mergedProfile);
            if (!aliasesChanged && !profileChanged)
            {
                continue;
            }

            var patchedDossier = dossier with
            {
                Aliases = aliasExamples.Keys.OrderBy(alias => alias, StringComparer.OrdinalIgnoreCase).ToArray(),
                AliasExamples = aliasExamples,
                Profile = mergedProfile
            };
            ReplaceDossier(changedDossiers, patchedDossier);
            dossiersById[dossier.CharacterId] = patchedDossier;
            changed = true;
            appliedCount++;
        }

        if (proposalCount > 0)
        {
            progress?.Report(new CharacterBibleWorkflowProgress(
                "patch",
                $"Dossier patching finished: {appliedCount}/{proposalCount} patches applied."));
        }

        var patchedDossiers = plan.ProjectedDossiers with { Characters = changedDossiers };
        return plan with
        {
            ProjectedDossiers = patchedDossiers,
            Changed = changed,
            Operations = UpdateReplaceDossiersOperation(plan.Operations, patchedDossiers, changed)
        };
    }

    private async Task<DossierReviewResult> ReviewPatchAsync(
        CharacterDossier dossier,
        IReadOnlyList<CharacterBibleDossierPatchCandidate> candidates,
        DossierPatchProposal proposal,
        CancellationToken cancellationToken)
    {
        var evidenceContexts = candidates
            .SelectMany(candidate => candidate.EvidenceContexts)
            .DistinctBy(context => $"{context.Pointer}\u001f{context.AnchorExcerpt}", StringComparer.Ordinal)
            .ToArray();

        try
        {
            return await reviewerModelClient.ReviewAsync(
                new DossierReviewModelRequest(
                    reviewerPromptBuilder.BuildSystemPrompt(),
                    reviewerPromptBuilder.BuildUserPrompt(dossier, proposal, evidenceContexts)),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Dossier patch review failed for character {CharacterId}. Patch rejected.", dossier.CharacterId);
            return new DossierReviewResult
            {
                Verdict = "reject_patch",
                Issues = ["Reviewer call failed."]
            };
        }
    }

    private IReadOnlyList<DossierPatchGroup> BuildPatchGroups(CharacterBibleCommitPlan plan)
    {
        var groups = new List<DossierPatchGroupBuilder>();
        var groupsByCharacterId = new Dictionary<string, DossierPatchGroupBuilder>(StringComparer.Ordinal);

        for (var index = 0; index < plan.Decisions.Count && index < plan.Candidates.Count; index++)
        {
            var decision = plan.Decisions[index];
            if (decision.Kind is not CharacterBibleDecisionKind.Existing and not CharacterBibleDecisionKind.New
                || string.IsNullOrWhiteSpace(decision.CharacterId))
            {
                continue;
            }

            var characterId = decision.CharacterId.Trim();
            if (!groupsByCharacterId.TryGetValue(characterId, out var group))
            {
                group = new DossierPatchGroupBuilder(characterId, decision);
                groupsByCharacterId[characterId] = group;
                groups.Add(group);
            }

            var candidate = plan.Candidates[index];
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
                yield return new DossierPatchGroup(group.CharacterId, group.Decision, batch.ToArray());
                batch = [];
                batchBytes = 0;
            }

            batch.Add(candidate);
            batchBytes += candidateBytes;
        }

        if (batch.Count > 0)
        {
            yield return new DossierPatchGroup(group.CharacterId, group.Decision, batch.ToArray());
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

    private static IReadOnlyList<CharacterBibleCommitOperation> UpdateReplaceDossiersOperation(
        IReadOnlyList<CharacterBibleCommitOperation> operations,
        CharacterDossiers patchedDossiers,
        bool changed)
    {
        if (!changed)
        {
            return operations;
        }

        var updated = new List<CharacterBibleCommitOperation>(operations.Count + 1);
        var replaced = false;
        foreach (var operation in operations)
        {
            if (operation.Kind == CharacterBibleCommitOperationKind.ReplaceDossiers)
            {
                updated.Add(operation with { ReplacementDossiers = patchedDossiers });
                replaced = true;
            }
            else
            {
                updated.Add(operation);
            }
        }

        if (!replaced)
        {
            updated.Insert(0, new CharacterBibleCommitOperation(
                CharacterBibleCommitOperationKind.ReplaceDossiers,
                ReplacementDossiers: patchedDossiers));
        }

        return updated;
    }

    private static CharacterProfile MergeProfileAdditions(CharacterProfile? existing, DossierProfilePatch? patch)
    {
        if (patch is null)
        {
            return CharacterProfile.Normalize(existing);
        }

        var normalizedExisting = CharacterProfile.Normalize(existing);
        return CharacterProfile.Normalize(new CharacterProfile(
            MergeProfileField(normalizedExisting.Appearance, patch.Appearance),
            MergeProfileField(normalizedExisting.StatusAndCompetence, patch.StatusAndCompetence),
            MergeProfileField(normalizedExisting.PsychologicalProfile, patch.PsychologicalProfile),
            MergeProfileField(normalizedExisting.SpeechAndCommunication, patch.SpeechAndCommunication)));
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

    private static Dictionary<string, string> MergeAliases(
        IReadOnlyDictionary<string, string>? currentAliasExamples,
        IReadOnlyList<CharacterBibleDossierPatchCandidate> candidates,
        IReadOnlyList<string>? aliasesToAdd)
    {
        var merged = new Dictionary<string, string>(currentAliasExamples ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var alias in aliasesToAdd ?? [])
        {
            var normalizedAlias = NullIfWhiteSpace(alias);
            if (normalizedAlias is null || merged.ContainsKey(normalizedAlias))
            {
                continue;
            }

            if (!TryGetAliasEvidence(candidates, normalizedAlias, out var evidence)
                || string.IsNullOrWhiteSpace(evidence.Excerpt))
            {
                continue;
            }

            merged[normalizedAlias] = evidence.Excerpt.Trim();
        }

        return merged;
    }

    private static bool TryGetAliasEvidence(
        IReadOnlyList<CharacterBibleDossierPatchCandidate> candidates,
        string alias,
        out CharacterBibleCandidateEvidence evidence)
    {
        foreach (var patchCandidate in candidates)
        {
            if (patchCandidate.Candidate.AliasEvidence.TryGetValue(alias, out var found))
            {
                evidence = found;
                return true;
            }
        }

        evidence = default!;
        return false;
    }

    private static bool HasSameAliasExamples(
        IReadOnlyDictionary<string, string>? left,
        IReadOnlyDictionary<string, string>? right)
    {
        var leftItems = left ?? new Dictionary<string, string>();
        var rightItems = right ?? new Dictionary<string, string>();
        return leftItems.Count == rightItems.Count
            && leftItems.All(item =>
                rightItems.TryGetValue(item.Key, out var value)
                && string.Equals(item.Value, value, StringComparison.Ordinal));
    }

    private static void ReplaceDossier(List<CharacterDossier> dossiers, CharacterDossier updatedDossier)
    {
        var index = dossiers.FindIndex(dossier => string.Equals(dossier.CharacterId, updatedDossier.CharacterId, StringComparison.Ordinal));
        if (index < 0)
        {
            return;
        }

        dossiers[index] = updatedDossier;
    }

    private sealed record DossierPatchGroup(
        string CharacterId,
        CharacterBibleResolverDecision Decision,
        IReadOnlyList<CharacterBibleDossierPatchCandidate> Candidates);

    private sealed class DossierPatchGroupBuilder(
        string characterId,
        CharacterBibleResolverDecision decision)
    {
        public string CharacterId { get; } = characterId;

        public CharacterBibleResolverDecision Decision { get; } = decision;

        public List<CharacterBibleDossierPatchCandidate> Candidates { get; } = [];
    }
}
