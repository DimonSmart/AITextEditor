using AiTextEditor.Core.Model;

namespace AiTextEditor.Agent;

public sealed record CharacterBibleWorkflowRequest(
    IReadOnlyCollection<string>? ChangedPointers = null,
    string? WorkflowRunId = null);

public sealed record CharacterBibleWorkflowResult(
    CharacterDossiers Dossiers,
    string Status,
    int ChangedPointerCount,
    int ParagraphCount,
    int CandidateCount,
    int DecisionCount,
    int AmbiguousDecisionCount,
    IReadOnlyList<CharacterBibleResolverDecision> Decisions,
    Exception? Failure = null);

public sealed record CharacterBibleParagraph(
    string Pointer,
    string Text);

public sealed record CharacterBibleTraversalResult(
    CharacterBibleWorkflowRequest Request,
    IReadOnlyList<CharacterBibleParagraph> Paragraphs);

public sealed record CharacterBibleCharacterCandidate(
    string CanonicalName,
    string Gender,
    IReadOnlyDictionary<string, string> AliasExamples,
    string Description);

public sealed record CharacterBibleExtractionResult(
    CharacterBibleWorkflowRequest Request,
    IReadOnlyList<CharacterBibleParagraph> Paragraphs,
    IReadOnlyList<CharacterBibleCharacterCandidate> Candidates,
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

public sealed record CharacterBibleCommitPlan(
    CharacterBibleWorkflowRequest Request,
    CharacterDossiers ProjectedDossiers,
    bool Changed,
    int ParagraphCount,
    int CandidateCount,
    IReadOnlyList<CharacterBibleResolverDecision> Decisions,
    Exception? Failure = null);
