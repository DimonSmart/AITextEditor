using AiTextEditor.Agent.CharacterBible.Extraction;
using AiTextEditor.Core.Model;

namespace AiTextEditor.Agent.CharacterBible.Resolution;

internal sealed class CharacterBibleCommitPlanBuilder
{
    private const int MaxIncrementalNewCharacterLevel = 4;

    private readonly CharacterBibleExtractionLimits limits;

    public CharacterBibleCommitPlanBuilder(CharacterBibleExtractionLimits limits)
    {
        this.limits = limits ?? throw new ArgumentNullException(nameof(limits));
    }

    public async Task<CharacterBibleCommitPlan> BuildAsync(
        CharacterBibleWorkflowInput request,
        CharacterDossiers baseDossiers,
        int paragraphCount,
        IReadOnlyList<CharacterBibleCharacterCandidate> candidates,
        Func<CharacterDossiers, CharacterBibleCharacterCandidate, CancellationToken, Task<IdentityResolutionDecision>> resolveIdentity,
        IProgress<CharacterBibleWorkflowProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(baseDossiers);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(resolveIdentity);

        if (candidates.Count == 0)
        {
            progress?.Report(new CharacterBibleWorkflowProgress(
                "resolve",
                "No candidates to resolve."));
            return new CharacterBibleCommitPlan(
                request,
                baseDossiers,
                false,
                paragraphCount,
                0,
                [],
                [],
                [],
                CharacterBibleModelResponseErrorStatistics.Empty);
        }

        var projectionIndex = new CharacterBibleDossierProjectionIndex(baseDossiers);
        var importanceAccumulator = new CharacterImportanceAccumulator();
        var createdCharacterIds = new HashSet<string>(StringComparer.Ordinal);
        var resolverDecisions = new List<CharacterBibleResolverDecision>(candidates.Count);
        var changed = false;

        for (var index = 0; index < candidates.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = candidates[index];
            var currentArchive = projectionIndex.ToDossiers(baseDossiers, maxCharacters: null);
            var identityDecision = await resolveIdentity(currentArchive, candidate, cancellationToken).ConfigureAwait(false);
            var hit = CharacterBibleExtractionMapper.ToCharacterExtractionCharacter(candidate);

            changed |= ApplyDecision(
                projectionIndex,
                hit,
                identityDecision,
                importanceAccumulator,
                createdCharacterIds,
                out var resolverDecision);
            resolverDecisions.Add(resolverDecision);
            progress?.Report(new CharacterBibleWorkflowProgress(
                "resolve",
                $"Resolved candidate {index + 1}/{candidates.Count}: {candidate.CanonicalName} -> {resolverDecision.Kind}."));
        }

        changed |= ApplyImportanceLevels(
            projectionIndex,
            request.ChangedPointers is null,
            importanceAccumulator.Scores,
            createdCharacterIds);

        var projectedDossiers = changed
            ? projectionIndex.ToDossiers(baseDossiers, limits.MaxCharacters)
            : baseDossiers;

        var operations = BuildOperations(projectedDossiers, changed, candidates, resolverDecisions);

        return new CharacterBibleCommitPlan(
            request,
            projectedDossiers,
            changed,
            paragraphCount,
            candidates.Count,
            candidates,
            resolverDecisions,
            operations,
            CharacterBibleModelResponseErrorStatistics.Empty);
    }

    private static IReadOnlyList<CharacterBibleCommitOperation> BuildOperations(
        CharacterDossiers projectedDossiers,
        bool changed,
        IReadOnlyList<CharacterBibleCharacterCandidate> candidates,
        IReadOnlyList<CharacterBibleResolverDecision> decisions)
    {
        var operations = new List<CharacterBibleCommitOperation>();
        if (changed)
        {
            operations.Add(new CharacterBibleCommitOperation(
                CharacterBibleCommitOperationKind.ReplaceDossiers,
                ReplacementDossiers: projectedDossiers));
        }

        var evidenceEntries = BuildEvidenceIndexEntries(candidates, decisions);
        if (evidenceEntries.Count > 0)
        {
            operations.Add(new CharacterBibleCommitOperation(
                CharacterBibleCommitOperationKind.AddEvidenceIndexEntries,
                EvidenceIndexEntries: evidenceEntries));
        }

        for (var index = 0; index < candidates.Count && index < decisions.Count; index++)
        {
            var candidate = candidates[index];
            var decision = decisions[index];
            if (decision.Kind is CharacterBibleDecisionKind.Ambiguous or CharacterBibleDecisionKind.IdentityConflict)
            {
                operations.Add(new CharacterBibleCommitOperation(
                    CharacterBibleCommitOperationKind.AddIdentityConflict,
                    IdentityConflict: new IdentityConflictRecord(
                        candidate.CandidateId,
                        candidate.CanonicalName,
                        decision.CandidateIds,
                        decision.Reason,
                        decision.SplitProposal?.Kind,
                        decision.SplitProposal?.Shards?
                            .Select(shard => shard.Name)
                            .Where(name => !string.IsNullOrWhiteSpace(name))
                            .Select(name => name!.Trim())
                            .ToArray(),
                        decision.SplitProposal?.Reason)));
                operations.Add(new CharacterBibleCommitOperation(
                    CharacterBibleCommitOperationKind.AddSuspectArchiveEntry,
                    SuspectArchiveEntry: new SuspectArchiveEntry(
                        candidate.CandidateId,
                        candidate.CanonicalName,
                        candidate.Gender,
                        candidate.AliasExamples.Keys.ToArray(),
                        candidate.Evidence.Select(evidence => new CharacterEvidenceIndexEntry(
                            evidence.Pointer,
                            evidence.Excerpt,
                            CandidateId: candidate.CandidateId)).ToArray(),
                        decision.Reason)));
            }
            else if (decision.Kind == CharacterBibleDecisionKind.Defer)
            {
                operations.Add(new CharacterBibleCommitOperation(
                    CharacterBibleCommitOperationKind.AddDeferredCandidate,
                    SuspectArchiveEntry: new SuspectArchiveEntry(
                        candidate.CandidateId,
                        candidate.CanonicalName,
                        candidate.Gender,
                        candidate.AliasExamples.Keys.ToArray(),
                        candidate.Evidence.Select(evidence => new CharacterEvidenceIndexEntry(
                            evidence.Pointer,
                            evidence.Excerpt,
                            CandidateId: candidate.CandidateId)).ToArray(),
                        decision.Reason)));
            }
        }

        operations.Add(new CharacterBibleCommitOperation(
            CharacterBibleCommitOperationKind.AddAuditTrailEntry,
            AuditTrailEntry: new CharacterBibleAuditEntry(
                DateTimeOffset.UtcNow,
                "character_bible_commit_plan",
                projectedDossiers.DossiersId,
                $"Candidates: {candidates.Count}; decisions: {decisions.Count}; changed: {changed}.")));

        return operations;
    }

    private static IReadOnlyList<CharacterEvidenceIndexEntry> BuildEvidenceIndexEntries(
        IReadOnlyList<CharacterBibleCharacterCandidate> candidates,
        IReadOnlyList<CharacterBibleResolverDecision> decisions)
    {
        var entries = new List<CharacterEvidenceIndexEntry>();
        for (var index = 0; index < candidates.Count && index < decisions.Count; index++)
        {
            var candidate = candidates[index];
            var decision = decisions[index];
            foreach (var evidence in candidate.Evidence)
            {
                entries.Add(new CharacterEvidenceIndexEntry(
                    evidence.Pointer,
                    evidence.Excerpt,
                    decision.CharacterId,
                    candidate.CandidateId));
            }
        }

        return entries;
    }

    private static bool ApplyDecision(
        CharacterBibleDossierProjectionIndex projectionIndex,
        CharacterExtractionCharacter hit,
        IdentityResolutionDecision identityDecision,
        CharacterImportanceAccumulator importanceAccumulator,
        ISet<string> createdCharacterIds,
        out CharacterBibleResolverDecision resolverDecision)
    {
        var canonicalName = hit.CanonicalName?.Trim() ?? string.Empty;

        if (identityDecision.Kind == IdentityResolutionKind.Ambiguous)
        {
            resolverDecision = new CharacterBibleResolverDecision(
                canonicalName,
                CharacterBibleDecisionKind.Ambiguous,
                null,
                identityDecision.AlternativeEntryIds,
                identityDecision.Reason);
            return false;
        }

        if (identityDecision.Kind == IdentityResolutionKind.Defer)
        {
            resolverDecision = new CharacterBibleResolverDecision(
                canonicalName,
                CharacterBibleDecisionKind.Defer,
                null,
                identityDecision.AlternativeEntryIds,
                identityDecision.Reason);
            return false;
        }

        if (identityDecision.Kind == IdentityResolutionKind.IdentityConflict)
        {
            resolverDecision = new CharacterBibleResolverDecision(
                canonicalName,
                CharacterBibleDecisionKind.IdentityConflict,
                null,
                identityDecision.AlternativeEntryIds,
                identityDecision.Reason)
            {
                SplitProposal = identityDecision.SplitProposal
            };
            return false;
        }

        var created = identityDecision.Kind == IdentityResolutionKind.New;
        var projection = created
            ? projectionIndex.AddCandidate(hit)
            : projectionIndex.GetRequired(identityDecision.TargetEntryId
                ?? throw new InvalidOperationException("Existing identity decision is missing target entry id."));

        var changed = created;
        importanceAccumulator.AddResolved(projection.CharacterId);

        if (created)
        {
            createdCharacterIds.Add(projection.CharacterId);
        }

        var nameChanged = projection.RefineCanonicalName(hit, identityDecision.ExactNameMatch);
        changed |= nameChanged;

        var anyAliasChanged = projection.MergeAliases(hit, identityDecision.ExactNameMatch);
        changed |= anyAliasChanged;

        changed |= projection.SetGenderIfUnknown(hit.Gender);

        resolverDecision = new CharacterBibleResolverDecision(
            canonicalName,
            created ? CharacterBibleDecisionKind.New : CharacterBibleDecisionKind.Existing,
            projection.CharacterId,
            [],
            identityDecision.Reason);

        return changed;
    }

    private static bool ApplyImportanceLevels(
        CharacterBibleDossierProjectionIndex projectionIndex,
        bool isFullGeneration,
        IReadOnlyDictionary<string, int> activityScores,
        IReadOnlySet<string> createdCharacterIds)
    {
        if (activityScores.Count == 0)
        {
            return false;
        }

        var maxScore = activityScores.Values.Max();
        var changed = false;

        foreach (var (characterId, score) in activityScores)
        {
            if (!isFullGeneration && !createdCharacterIds.Contains(characterId))
            {
                continue;
            }

            var level = CharacterImportance.ToLevel(score, maxScore);
            if (!isFullGeneration)
            {
                level = Math.Min(level, MaxIncrementalNewCharacterLevel);
            }

            changed |= projectionIndex.SetImportanceLevelIfMissing(characterId, level);
        }

        return changed;
    }

    private sealed class CharacterImportanceAccumulator
    {
        private readonly Dictionary<string, int> scores = new(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, int> Scores => scores;

        public void AddResolved(string characterId)
        {
            if (string.IsNullOrWhiteSpace(characterId))
            {
                return;
            }

            scores[characterId] = scores.TryGetValue(characterId, out var current)
                ? current + 1
                : 1;
        }
    }
}
