using AiTextEditor.Core.Model;
using AiTextEditor.Agent.CharacterBible.Patching;

namespace AiTextEditor.Agent.CharacterBible.Resolution;

public interface ICharacterArchiveSearchTool
{
    Task<IReadOnlyList<CharacterArchiveSearchHit>> SearchCharactersAsync(
        string query,
        int limit,
        CancellationToken cancellationToken);
}

public sealed record CharacterArchiveSearchHit(
    string EntryId,
    string Name,
    string Gender,
    IReadOnlyList<string> Aliases,
    string Identity,
    double Score);

internal enum CharacterArchiveEntryKind
{
    Character,
    Suspect
}

internal sealed record CharacterArchiveSearchRequest(
    string CandidateName,
    IReadOnlyList<string> Aliases,
    string Gender,
    IReadOnlyList<CharacterBibleCandidateEvidence> Evidence,
    int MaxResults);

internal sealed record CharacterArchiveHit(
    string EntryId,
    CharacterArchiveEntryKind EntryKind,
    string Name,
    IReadOnlyList<string> Aliases,
    string Gender,
    string ProfileSnippet,
    IReadOnlyList<string> MatchReasons,
    int Score);

internal enum IdentityResolutionKind
{
    Existing,
    New,
    Ambiguous,
    Defer,
    IdentityConflict
}

internal sealed record IdentityResolutionDecision(
    IdentityResolutionKind Kind,
    string? TargetEntryId,
    IReadOnlyList<string> AlternativeEntryIds,
    string Reason,
    bool ExactNameMatch)
{
    public SplitProposal? SplitProposal { get; init; }

    public static IdentityResolutionDecision Existing(CharacterArchiveHit hit)
    {
        return new IdentityResolutionDecision(
            IdentityResolutionKind.Existing,
            hit.EntryId,
            [],
            "Matched by existing name or alias key.",
            hit.MatchReasons.Contains(CharacterArchiveSearchService.ExactNameMatchReason, StringComparer.Ordinal));
    }

    public static IdentityResolutionDecision New()
    {
        return new IdentityResolutionDecision(
            IdentityResolutionKind.New,
            null,
            [],
            "No existing name or alias match was found.",
            ExactNameMatch: true);
    }

    public static IdentityResolutionDecision Ambiguous(IReadOnlyList<CharacterArchiveHit> hits)
    {
        return new IdentityResolutionDecision(
            IdentityResolutionKind.Ambiguous,
            null,
            hits.Select(hit => hit.EntryId).ToArray(),
            "Multiple existing dossiers matched the same name or alias key.",
            ExactNameMatch: false);
    }

    public static IdentityResolutionDecision Ambiguous(
        IReadOnlyList<string> alternativeEntryIds,
        string reason)
    {
        return new IdentityResolutionDecision(
            IdentityResolutionKind.Ambiguous,
            null,
            alternativeEntryIds,
            reason,
            ExactNameMatch: false);
    }

    public static IdentityResolutionDecision Defer(
        IReadOnlyList<string> alternativeEntryIds,
        string reason)
    {
        return new IdentityResolutionDecision(
            IdentityResolutionKind.Defer,
            null,
            alternativeEntryIds,
            reason,
            ExactNameMatch: false);
    }

    public static IdentityResolutionDecision IdentityConflict(
        IReadOnlyList<string> alternativeEntryIds,
        string reason)
    {
        return new IdentityResolutionDecision(
            IdentityResolutionKind.IdentityConflict,
            null,
            alternativeEntryIds,
            reason,
            ExactNameMatch: false);
    }

    public static IdentityResolutionDecision Existing(
        string targetEntryId,
        string reason)
    {
        return new IdentityResolutionDecision(
            IdentityResolutionKind.Existing,
            targetEntryId,
            [],
            reason,
            ExactNameMatch: true);
    }

    public static IdentityResolutionDecision New(string reason)
    {
        return new IdentityResolutionDecision(
            IdentityResolutionKind.New,
            null,
            [],
            reason,
            ExactNameMatch: true);
    }
}
