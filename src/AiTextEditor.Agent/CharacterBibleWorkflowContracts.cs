using AiTextEditor.Core.Model;

namespace AiTextEditor.Agent;

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
    Exception? Failure = null);

public sealed record CharacterBibleWorkflowProgress(
    string Stage,
    string Message);

public sealed record TextFragment(
    string Pointer,
    string Text);

public sealed record CharacterBibleTraversalResult(
    CharacterBibleWorkflowInput Request,
    IReadOnlyList<TextFragment> Paragraphs);

public sealed record CharacterBibleCharacterCandidate(
    string CanonicalName,
    string Gender,
    IReadOnlyDictionary<string, string> AliasExamples,
    string Description,
    CharacterProfile Profile);

public sealed record CharacterBibleExtractionResult(
    CharacterBibleWorkflowInput Request,
    IReadOnlyList<TextFragment> Paragraphs,
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
    CharacterBibleWorkflowInput Request,
    CharacterDossiers ProjectedDossiers,
    bool Changed,
    int ParagraphCount,
    int CandidateCount,
    IReadOnlyList<CharacterBibleResolverDecision> Decisions,
    Exception? Failure = null);
