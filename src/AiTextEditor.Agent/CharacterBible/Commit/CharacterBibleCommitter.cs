using AiTextEditor.Core.Model;
using AiTextEditor.Core.Services;

namespace AiTextEditor.Agent.CharacterBible.Commit;

internal sealed class CharacterBibleCommitter
{
    private readonly CharacterDossierService dossierService;

    public CharacterBibleCommitter(CharacterDossierService dossierService)
    {
        this.dossierService = dossierService ?? throw new ArgumentNullException(nameof(dossierService));
    }

    public CharacterDossiers Commit(CharacterBibleCommitPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var current = dossierService.GetDossiers();
        var next = current;

        foreach (var operation in plan.Operations)
        {
            next = Apply(next, operation);
        }

        if (!ReferenceEquals(next, current) && !HasSameContent(next, current))
        {
            dossierService.ReplaceDossiers(next);
        }

        return dossierService.GetDossiers();
    }

    private static CharacterDossiers Apply(
        CharacterDossiers current,
        CharacterBibleCommitOperation operation)
    {
        return operation.Kind switch
        {
            CharacterBibleCommitOperationKind.ReplaceDossiers when operation.ReplacementDossiers is not null
                => current with { Characters = operation.ReplacementDossiers.Characters },
            CharacterBibleCommitOperationKind.AddEvidenceIndexEntries
                => current with { EvidenceIndex = AddEvidence(current.EvidenceIndex, operation.EvidenceIndexEntries) },
            CharacterBibleCommitOperationKind.AddSuspectArchiveEntry when operation.SuspectArchiveEntry is not null
                => current with { SuspectArchive = AddDistinct(current.SuspectArchive, operation.SuspectArchiveEntry, entry => entry.CandidateId) },
            CharacterBibleCommitOperationKind.AddDeferredCandidate when operation.SuspectArchiveEntry is not null
                => current with { SuspectArchive = AddDistinct(current.SuspectArchive, operation.SuspectArchiveEntry, entry => entry.CandidateId) },
            CharacterBibleCommitOperationKind.AddIdentityConflict when operation.IdentityConflict is not null
                => current with { IdentityConflicts = AddDistinct(current.IdentityConflicts, operation.IdentityConflict, conflict => conflict.CandidateId) },
            CharacterBibleCommitOperationKind.AddAuditTrailEntry when operation.AuditTrailEntry is not null
                => current with { AuditTrail = AddAudit(current.AuditTrail, operation.AuditTrailEntry) },
            _ => current
        };
    }

    private static IReadOnlyList<CharacterEvidenceIndexEntry> AddEvidence(
        IReadOnlyList<CharacterEvidenceIndexEntry>? existing,
        IReadOnlyList<CharacterEvidenceIndexEntry>? additions)
    {
        if (additions is null || additions.Count == 0)
        {
            return existing ?? [];
        }

        return (existing ?? [])
            .Concat(additions)
            .DistinctBy(entry => $"{entry.Pointer}\u001f{entry.Excerpt}\u001f{entry.CharacterId}\u001f{entry.CandidateId}", StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<T> AddDistinct<T>(
        IReadOnlyList<T>? existing,
        T addition,
        Func<T, string> keySelector)
    {
        var items = (existing ?? []).ToList();
        var key = keySelector(addition);
        if (items.Any(item => string.Equals(keySelector(item), key, StringComparison.Ordinal)))
        {
            return items;
        }

        items.Add(addition);
        return items;
    }

    private static IReadOnlyList<CharacterBibleAuditEntry> AddAudit(
        IReadOnlyList<CharacterBibleAuditEntry>? existing,
        CharacterBibleAuditEntry addition)
    {
        return [.. existing ?? [], addition];
    }

    private static bool HasSameContent(CharacterDossiers left, CharacterDossiers right)
    {
        return ReferenceEquals(left.Characters, right.Characters)
            && ReferenceEquals(left.SuspectArchive, right.SuspectArchive)
            && ReferenceEquals(left.EvidenceIndex, right.EvidenceIndex)
            && ReferenceEquals(left.IdentityConflicts, right.IdentityConflicts)
            && ReferenceEquals(left.AuditTrail, right.AuditTrail);
    }
}
