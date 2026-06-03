using System.Collections.Generic;

namespace AiTextEditor.Core.Model;

public sealed record CharacterDossiers(
    string DossiersId,
    int Version,
    IReadOnlyList<CharacterDossier> Characters,
    int NextCharacterId = 1,
    IReadOnlyList<SuspectArchiveEntry>? SuspectArchive = null,
    IReadOnlyList<CharacterEvidenceIndexEntry>? EvidenceIndex = null,
    IReadOnlyList<IdentityConflictRecord>? IdentityConflicts = null,
    IReadOnlyList<CharacterBibleAuditEntry>? AuditTrail = null);

public sealed record SuspectArchiveEntry(
    string CanonicalName,
    string Gender,
    IReadOnlyList<string> ObservedNameForms,
    IReadOnlyList<CharacterEvidenceIndexEntry> Evidence,
    string Reason);

public sealed record CharacterEvidenceIndexEntry(
    string Pointer,
    string Excerpt,
    int? CharacterId = null);

public sealed record IdentityConflictRecord(
    string CanonicalName,
    IReadOnlyList<int> AlternativeCharacterIds,
    string Reason,
    string? SplitProposalKind = null,
    IReadOnlyList<string>? SplitShardNames = null,
    string? SplitProposalReason = null);

public sealed record CharacterBibleAuditEntry(
    DateTimeOffset Timestamp,
    string Operation,
    string TargetId,
    string Reason);
