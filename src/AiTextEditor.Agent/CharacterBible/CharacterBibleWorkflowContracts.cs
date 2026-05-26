using AiTextEditor.Core.Model;

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
    bool AlwaysVisible = false);

public sealed record CharacterBibleModelResponseErrorStatistics(
    int ParseErrorCount = 0,
    int RecoveredCount = 0,
    int FailedRecoveryCount = 0,
    int RetryCount = 0,
    int RetrySucceededCount = 0,
    int SkippedBatchCount = 0,
    int SkippedParagraphCount = 0)
{
    public static CharacterBibleModelResponseErrorStatistics Empty { get; } = new();

    public int TotalErrorCount => ParseErrorCount + FailedRecoveryCount + SkippedBatchCount;
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
    CharacterProfile Profile);

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
    Defer
}

public sealed record CharacterBibleResolverDecision(
    string CanonicalName,
    CharacterBibleDecisionKind Kind,
    string? CharacterId,
    IReadOnlyList<string> CandidateIds,
    string Reason);

internal sealed record CharacterBibleCommitPlan(
    CharacterBibleWorkflowInput Request,
    CharacterDossiers ProjectedDossiers,
    bool Changed,
    int ParagraphCount,
    int CandidateCount,
    IReadOnlyList<CharacterBibleResolverDecision> Decisions,
    CharacterBibleModelResponseErrorStatistics ModelResponseErrors,
    Exception? Failure = null);

internal sealed record CharacterBibleCandidateExtractionResult(
    IReadOnlyList<CharacterBibleCharacterCandidate> Candidates,
    CharacterBibleModelResponseErrorStatistics ModelResponseErrors);
