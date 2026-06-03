using AiTextEditor.Core.Model;
using AiTextEditor.Agent.CharacterBible.Diagnostics;

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
        Func<CharacterDossiers, CharacterBibleCharacterCandidate, int, CancellationToken, Task<IdentityResolutionDecision>> resolveIdentity,
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
        var createdCharacterIds = new HashSet<int>();
        var pendingCanonicalNameNormalization = new HashSet<int>();

        for (var index = 0; index < candidates.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = candidates[index];
            var candidateIndex = index + 1;
            CharacterBibleRunLogScope.Current?.Info(
                "resolve.candidate.start",
                $"candidateIndex={candidateIndex} total={candidates.Count} name={LogValueFormatter.Quote(candidate.CanonicalName)} gender={LogValueFormatter.Quote(candidate.Gender)} observedNameForms={LogValueFormatter.List(candidate.ObservedNameFormExamples.Keys)} pointers={LogValueFormatter.List(candidate.Evidence.Select(evidence => evidence.Pointer))}");
            var identityDecision = await resolveIdentity(session.Current, candidate, candidateIndex, cancellationToken).ConfigureAwait(false);

            CharacterBibleRunLogScope.Current?.Info(
                "resolve.decision",
                $"candidateIndex={candidateIndex} name={LogValueFormatter.Quote(candidate.CanonicalName)} decision={identityDecision.Kind.ToString().ToLowerInvariant()} characterId={LogValueFormatter.NullableId(identityDecision.CharacterId)} characterIds={LogValueFormatter.List(identityDecision.CharacterIds)} reason={LogValueFormatter.Quote(identityDecision.Reason)}");
            ApplyDecision(
                session,
                candidate,
                identityDecision,
                importanceAccumulator,
                createdCharacterIds,
                pendingCanonicalNameNormalization);
            var decision = session.Decisions[^1];
            CharacterBibleRunLogScope.Current?.Info(
                "resolve.apply",
                $"candidateIndex={candidateIndex} characterId={LogValueFormatter.NullableId(decision.CharacterId)} decision={decision.Kind.ToString().ToLowerInvariant()} evidenceAdded={candidate.Evidence.Count}");
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
            CharacterBibleModelResponseErrorStatistics.Empty,
            pendingCanonicalNameNormalization);
    }

    private static void ApplyDecision(
        CharacterDossierEditSession session,
        CharacterBibleCharacterCandidate candidate,
        IdentityResolutionDecision identityDecision,
        CharacterImportanceAccumulator importanceAccumulator,
        ISet<int> createdCharacterIds,
        ISet<int> pendingCanonicalNameNormalization)
    {
        var canonicalName = candidate.CanonicalName.Trim();
        CharacterBibleResolverDecision resolverDecision;

        if (identityDecision.Kind == IdentityResolutionKind.Ambiguous)
        {
            resolverDecision = new CharacterBibleResolverDecision(
                canonicalName,
                CharacterBibleDecisionKind.Ambiguous,
                null,
                identityDecision.CharacterIds,
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
                identityDecision.CharacterIds,
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
                identityDecision.CharacterIds,
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
            ? session.CreateCharacter(candidate)
            : session.GetRequired(identityDecision.CharacterId
                ?? throw new InvalidOperationException("Existing identity decision is missing character id."));

        importanceAccumulator.AddResolved(dossier.CharacterId);
        if (created)
        {
            createdCharacterIds.Add(dossier.CharacterId);
            pendingCanonicalNameNormalization.Add(dossier.CharacterId);
            CharacterBibleRunLogScope.Current?.Info(
                "archive.character.created",
                $"characterId={dossier.CharacterId} nextCharacterId={session.Current.NextCharacterId} name={LogValueFormatter.Quote(dossier.Name)}");
        }

        if (session.MergeObservedNameForms(dossier.CharacterId, candidate, identityDecision.ExactNameMatch))
        {
            pendingCanonicalNameNormalization.Add(dossier.CharacterId);
        }

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
                decision.CharacterId))
            .ToArray();
    }

    private static SuspectArchiveEntry BuildSuspectArchiveEntry(
        CharacterBibleCharacterCandidate candidate,
        string reason)
    {
        return new SuspectArchiveEntry(
            candidate.CanonicalName,
            candidate.Gender,
            candidate.ObservedNameFormExamples.Keys.ToArray(),
            candidate.Evidence.Select(evidence => new CharacterEvidenceIndexEntry(
                evidence.Pointer,
                evidence.Excerpt)).ToArray(),
            reason);
    }

    private static IdentityConflictRecord BuildIdentityConflict(
        CharacterBibleCharacterCandidate candidate,
        CharacterBibleResolverDecision decision)
    {
        return new IdentityConflictRecord(
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
        IReadOnlyDictionary<int, int> activityScores,
        IReadOnlySet<int> createdCharacterIds)
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
        private readonly Dictionary<int, int> scores = [];

        public IReadOnlyDictionary<int, int> Scores => scores;

        public void AddResolved(int characterId)
        {
            scores[characterId] = scores.TryGetValue(characterId, out var current)
                ? current + 1
                : 1;
        }
    }
}
