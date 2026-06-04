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
    bool IsError = false,
    CharacterDossiers? DossiersSnapshot = null);

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
    IReadOnlyDictionary<string, string> ObservedNameFormExamples,
    IReadOnlyList<CharacterBibleCandidateEvidence> Evidence)
{
    public IReadOnlyDictionary<string, CharacterBibleCandidateEvidence> ObservedNameFormEvidence { get; init; }
        = new Dictionary<string, CharacterBibleCandidateEvidence>(CharacterNameFormComparer.Instance);
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
    int? CharacterId,
    IReadOnlyList<int> CandidateIds,
    string Reason)
{
    public SplitProposal? SplitProposal { get; init; }
}

internal sealed record CharacterBibleRunState(
    CharacterBibleWorkflowInput Request,
    CharacterDossierEditSession Catalog,
    int ParagraphCount,
    IReadOnlyList<CharacterBibleCharacterCandidate> Candidates,
    CharacterBibleModelResponseErrorStatistics ModelResponseErrors,
    IReadOnlySet<int>? PendingCanonicalNameNormalization = null,
    Exception? Failure = null);

internal sealed record CharacterBibleCandidateExtractionResult(
    IReadOnlyList<CharacterBibleCharacterCandidate> Candidates,
    CharacterBibleModelResponseErrorStatistics ModelResponseErrors);
