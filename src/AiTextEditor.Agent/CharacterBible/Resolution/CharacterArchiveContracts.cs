using System.ComponentModel;
using AiTextEditor.Agent.CharacterBible.Patching;

namespace AiTextEditor.Agent.CharacterBible.Resolution;

public interface ICharacterArchiveSearchTool
{
    [Description(CharacterArchiveSearchToolDescriptions.Tool)]
    Task<CharacterArchiveSearchResult> SearchCharactersAsync(
        [Description(CharacterArchiveSearchToolDescriptions.QueryParameter)]
        string query,
        [Description(CharacterArchiveSearchToolDescriptions.LimitParameter)]
        int limit,
        CancellationToken cancellationToken);
}

public sealed record CharacterArchiveSearchResult(
    string Query,
    int Limit,
    int ArchiveSize,
    int ReturnedCount,
    string Note,
    IReadOnlyList<CharacterArchiveSearchHit> Hits);

public sealed record CharacterArchiveSearchHit(
    int Rank,
    string EntryId,
    string Name,
    string Gender,
    IReadOnlyList<string> Aliases,
    string Identity,
    double Score);

public static class CharacterArchiveSearchResultNotes
{
    public const string ClosestEntriesMayBeUnrelated =
        "Returned entries are the closest available archive entries. They may all be unrelated when the archive is small. Score is vector similarity, not identity confidence.";
}

internal static class CharacterArchiveSearchToolDescriptions
{
    public const string Tool =
        "Searches the current in-memory character archive and returns the most similar archive entries.\n\n"
        + "Important:\n"
        + "- Results are retrieval candidates, not confirmed identity matches.\n"
        + "- The tool returns the closest entries available in the current archive.\n"
        + "- When the archive is small, all returned entries may be unrelated to the local candidate.\n"
        + "- A non-empty result list does not mean the candidate already exists.\n"
        + "- Score is vector similarity, not identity confidence.\n"
        + "- Use returned entries only as options to compare against the candidate and supplied evidence.\n"
        + "- If none of the returned entries clearly represents the same character, return \"new\" or \"defer\".";

    public const string QueryParameter =
        "Plain text query built primarily from the candidate name and observed aliases. Do not add generic words such as character, book, story, персонаж, книга. Do not add names from previous search hits.";

    public const string LimitParameter =
        "Maximum number of retrieval candidates to return. More results do not imply higher identity confidence.";
}

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
