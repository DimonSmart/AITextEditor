using AiTextEditor.Core.Model;
using AiTextEditor.Agent.CharacterBible.Patching;

namespace AiTextEditor.Agent.CharacterBible;

public sealed record CharacterBibleWorkflowInput(
    IReadOnlyCollection<string>? ChangedPointers = null
    );

public sealed record CharacterBibleWorkflowOutput(
    CharacterDossiers Dossiers,
    string Status,
    int ChangedPointerCount,
    int ParagraphCount,
    int CandidateCount,
    int DecisionCount,
    int AmbiguousDecisionCount,
    IReadOnlyList<CharacterBibleResolverDecision> Decisions,
    CharacterBibleModelResponseErrorStatistics? ModelResponseErrors = null,
    Exception? Failure = null);

public sealed record CharacterBibleWorkflowProgress(
    string Stage,
    string Message,
    string? CopyText = null,
    string? CopyLabel = null,
    bool AlwaysVisible = false,
    bool IsError = false);

public sealed record CharacterBibleModelResponseErrorStatistics(
    int ParseErrorCount = 0,
    int ContractErrorCount = 0,
    int RetryCount = 0,
    int RetrySucceededCount = 0,
    int SkippedBatchCount = 0,
    int SkippedParagraphCount = 0)
{
    public static CharacterBibleModelResponseErrorStatistics Empty { get; } = new();

    public int TotalErrorCount => ParseErrorCount + ContractErrorCount + SkippedBatchCount;
}

internal sealed record TextFragment(
    string Pointer,
    string Text);

internal sealed record CharacterBibleTraversalResult(
    CharacterBibleWorkflowInput Request,
    IReadOnlyList<TextFragment> Paragraphs);

internal sealed record CharacterBibleCharacterCandidate(
    string CanonicalName,
    string Gender,
    IReadOnlyDictionary<string, string> AliasExamples,
    IReadOnlyList<CharacterBibleCandidateEvidence> Evidence)
{
    public string CandidateId { get; init; } = string.Empty;

    public IReadOnlyDictionary<string, CharacterBibleCandidateEvidence> AliasEvidence { get; init; }
        = new Dictionary<string, CharacterBibleCandidateEvidence>(StringComparer.OrdinalIgnoreCase);
}

internal sealed record CharacterBibleCandidateEvidence(
    string Pointer,
    string Excerpt);

internal sealed record CharacterBibleExtractionResult(
    CharacterBibleWorkflowInput Request,
    IReadOnlyList<TextFragment> Paragraphs,
    IReadOnlyList<CharacterBibleCharacterCandidate> Candidates,
    CharacterBibleModelResponseErrorStatistics ModelResponseErrors,
    Exception? Failure = null);

public enum CharacterBibleDecisionKind
{
    Existing,
    New,
    Ambiguous,
    Defer,
    IdentityConflict
}

public sealed record CharacterBibleResolverDecision(
    string CanonicalName,
    CharacterBibleDecisionKind Kind,
    string? CharacterId,
    IReadOnlyList<string> CandidateIds,
    string Reason)
{
    public SplitProposal? SplitProposal { get; init; }
}

internal sealed record CharacterBibleCommitPlan(
    CharacterBibleWorkflowInput Request,
    CharacterDossiers ProjectedDossiers,
    bool Changed,
    int ParagraphCount,
    int CandidateCount,
    IReadOnlyList<CharacterBibleCharacterCandidate> Candidates,
    IReadOnlyList<CharacterBibleResolverDecision> Decisions,
    IReadOnlyList<CharacterBibleCommitOperation> Operations,
    CharacterBibleModelResponseErrorStatistics ModelResponseErrors,
    Exception? Failure = null);

internal enum CharacterBibleCommitOperationKind
{
    ReplaceDossiers,
    AddSuspectArchiveEntry,
    AddIdentityConflict,
    AddDeferredCandidate,
    AddEvidenceIndexEntries,
    AddAuditTrailEntry
}

internal sealed record CharacterBibleCommitOperation(
    CharacterBibleCommitOperationKind Kind,
    Core.Model.CharacterDossiers? ReplacementDossiers = null,
    Core.Model.SuspectArchiveEntry? SuspectArchiveEntry = null,
    Core.Model.IdentityConflictRecord? IdentityConflict = null,
    IReadOnlyList<Core.Model.CharacterEvidenceIndexEntry>? EvidenceIndexEntries = null,
    Core.Model.CharacterBibleAuditEntry? AuditTrailEntry = null);

internal sealed record CharacterBibleCandidateExtractionResult(
    IReadOnlyList<CharacterBibleCharacterCandidate> Candidates,
    CharacterBibleModelResponseErrorStatistics ModelResponseErrors);
