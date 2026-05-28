using AiTextEditor.Core.Model;

namespace AiTextEditor.Agent.CharacterBible.Resolution;

internal sealed class CharacterBibleCandidateResolutionApplier
{
    private const int MaxIncrementalNewCharacterLevel = 4;

    private readonly CharacterBibleExtractionLimits limits;

    public CharacterBibleCandidateResolutionApplier(CharacterBibleExtractionLimits limits)
    {
        this.limits = limits ?? throw new ArgumentNullException(nameof(limits));
    }

    public async Task<CharacterBibleRunState> ResolveAndUpdateCatalogAsync(
        CharacterBibleWorkflowInput request,
        CharacterDossierEditSession session,
        int paragraphCount,
        IReadOnlyList<CharacterBibleCharacterCandidate> candidates,
        Func<CharacterDossiers, CharacterBibleCharacterCandidate, CancellationToken, Task<IdentityResolutionDecision>> resolveIdentity,
        IProgress<CharacterBibleWorkflowProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(resolveIdentity);

        if (candidates.Count == 0)
        {
            progress?.Report(new CharacterBibleWorkflowProgress(
                "resolve",
                "No candidates to resolve."));
            AddAuditTrailEntry(session, candidates.Count);
            return new CharacterBibleRunState(
                request,
                session,
                paragraphCount,
                [],
                CharacterBibleModelResponseErrorStatistics.Empty);
        }

        var importanceAccumulator = new CharacterImportanceAccumulator();
        var createdCharacterIds = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < candidates.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = candidates[index];
            var identityDecision = await resolveIdentity(session.Current, candidate, cancellationToken).ConfigureAwait(false);

            ApplyDecision(
                session,
                candidate,
                identityDecision,
                importanceAccumulator,
                createdCharacterIds);
            progress?.Report(new CharacterBibleWorkflowProgress(
                "resolve",
                $"Resolved candidate {index + 1}/{candidates.Count}: {candidate.CanonicalName} -> {session.Decisions[^1].Kind}."));
        }

        ApplyImportanceLevels(
            session,
            request.ChangedPointers is null,
            importanceAccumulator.Scores,
            createdCharacterIds);
        session.LimitCharacters(limits.MaxCharacters);
        AddAuditTrailEntry(session, candidates.Count);

        return new CharacterBibleRunState(
            request,
            session,
            paragraphCount,
            candidates,
            CharacterBibleModelResponseErrorStatistics.Empty);
    }

    private static void ApplyDecision(
        CharacterDossierEditSession session,
        CharacterBibleCharacterCandidate candidate,
        IdentityResolutionDecision identityDecision,
        CharacterImportanceAccumulator importanceAccumulator,
        ISet<string> createdCharacterIds)
    {
        var canonicalName = candidate.CanonicalName.Trim();
        CharacterBibleResolverDecision resolverDecision;

        if (identityDecision.Kind == IdentityResolutionKind.Ambiguous)
        {
            resolverDecision = new CharacterBibleResolverDecision(
                canonicalName,
                CharacterBibleDecisionKind.Ambiguous,
                null,
                identityDecision.AlternativeEntryIds,
                identityDecision.Reason);
            session.AddIdentityConflict(BuildIdentityConflict(candidate, resolverDecision));
            session.AddSuspectArchiveEntry(BuildSuspectArchiveEntry(candidate, resolverDecision.Reason));
            session.AddEvidenceIndexEntries(BuildEvidenceIndexEntries(candidate, resolverDecision));
            session.AddDecision(resolverDecision);
            return;
        }

        if (identityDecision.Kind == IdentityResolutionKind.Defer)
        {
            resolverDecision = new CharacterBibleResolverDecision(
                canonicalName,
                CharacterBibleDecisionKind.Defer,
                null,
                identityDecision.AlternativeEntryIds,
                identityDecision.Reason);
            session.AddSuspectArchiveEntry(BuildSuspectArchiveEntry(candidate, resolverDecision.Reason));
            session.AddEvidenceIndexEntries(BuildEvidenceIndexEntries(candidate, resolverDecision));
            session.AddDecision(resolverDecision);
            return;
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
            session.AddIdentityConflict(BuildIdentityConflict(candidate, resolverDecision));
            session.AddSuspectArchiveEntry(BuildSuspectArchiveEntry(candidate, resolverDecision.Reason));
            session.AddEvidenceIndexEntries(BuildEvidenceIndexEntries(candidate, resolverDecision));
            session.AddDecision(resolverDecision);
            return;
        }

        var created = identityDecision.Kind == IdentityResolutionKind.New;
        var dossier = created
            ? session.AddCandidate(candidate)
            : session.GetRequired(identityDecision.TargetEntryId
                ?? throw new InvalidOperationException("Existing identity decision is missing target entry id."));

        importanceAccumulator.AddResolved(dossier.CharacterId);
        if (created)
        {
            createdCharacterIds.Add(dossier.CharacterId);
        }

        session.RefineCanonicalName(dossier.CharacterId, candidate, identityDecision.ExactNameMatch);
        session.MergeAliases(dossier.CharacterId, candidate, identityDecision.ExactNameMatch);
        session.SetGenderIfUnknown(dossier.CharacterId, candidate.Gender);

        resolverDecision = new CharacterBibleResolverDecision(
            canonicalName,
            created ? CharacterBibleDecisionKind.New : CharacterBibleDecisionKind.Existing,
            dossier.CharacterId,
            [],
            identityDecision.Reason);
        session.AddEvidenceIndexEntries(BuildEvidenceIndexEntries(candidate, resolverDecision));
        session.AddDecision(resolverDecision);
    }

    private static IReadOnlyList<CharacterEvidenceIndexEntry> BuildEvidenceIndexEntries(
        CharacterBibleCharacterCandidate candidate,
        CharacterBibleResolverDecision decision)
    {
        return candidate.Evidence
            .Select(evidence => new CharacterEvidenceIndexEntry(
                evidence.Pointer,
                evidence.Excerpt,
                decision.CharacterId,
                candidate.CandidateId))
            .ToArray();
    }

    private static SuspectArchiveEntry BuildSuspectArchiveEntry(
        CharacterBibleCharacterCandidate candidate,
        string reason)
    {
        return new SuspectArchiveEntry(
            candidate.CandidateId,
            candidate.CanonicalName,
            candidate.Gender,
            candidate.AliasExamples.Keys.ToArray(),
            candidate.Evidence.Select(evidence => new CharacterEvidenceIndexEntry(
                evidence.Pointer,
                evidence.Excerpt,
                CandidateId: candidate.CandidateId)).ToArray(),
            reason);
    }

    private static IdentityConflictRecord BuildIdentityConflict(
        CharacterBibleCharacterCandidate candidate,
        CharacterBibleResolverDecision decision)
    {
        return new IdentityConflictRecord(
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
            decision.SplitProposal?.Reason);
    }

    private static void AddAuditTrailEntry(CharacterDossierEditSession session, int candidateCount)
    {
        session.AddAuditTrailEntry(new CharacterBibleAuditEntry(
            DateTimeOffset.UtcNow,
            "character_bible_run",
            session.Current.DossiersId,
            $"Candidates: {candidateCount}; decisions: {session.Decisions.Count}; changed: {session.Changed}."));
    }

    private static void ApplyImportanceLevels(
        CharacterDossierEditSession session,
        bool isFullGeneration,
        IReadOnlyDictionary<string, int> activityScores,
        IReadOnlySet<string> createdCharacterIds)
    {
        if (activityScores.Count == 0)
        {
            return;
        }

        var maxScore = activityScores.Values.Max();
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

            session.SetImportanceLevelIfMissing(characterId, level);
        }
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
