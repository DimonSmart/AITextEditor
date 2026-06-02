using System.ComponentModel;
using AiTextEditor.Agent.CharacterBible.Diagnostics;
using AiTextEditor.Agent.CharacterBible.VectorSearch;
using AiTextEditor.Core.Model;

namespace AiTextEditor.Agent.CharacterBible.Resolution;

internal sealed class CharacterArchiveSearchToolAdapter : ICharacterArchiveSearchTool
{
    private const int DefaultLimit = 5;

    private readonly CharacterDossiers currentArchive;
    private readonly ICharacterVectorSearchTool vectorSearchTool;
    private readonly string? candidateId;
    private readonly string? candidateName;
    private readonly HashSet<int> observedEntryIds = [];

    public CharacterArchiveSearchToolAdapter(
        CharacterDossiers currentArchive,
        ICharacterVectorSearchTool vectorSearchTool,
        string? candidateId = null,
        string? candidateName = null)
    {
        this.currentArchive = currentArchive ?? throw new ArgumentNullException(nameof(currentArchive));
        this.vectorSearchTool = vectorSearchTool ?? throw new ArgumentNullException(nameof(vectorSearchTool));
        this.candidateId = candidateId;
        this.candidateName = candidateName;
    }

    public IReadOnlySet<int> ObservedEntryIds => observedEntryIds;

    [Description(CharacterArchiveSearchToolDescriptions.Tool)]
    public async Task<CharacterArchiveSearchResult> SearchCharactersAsync(
        [Description(CharacterArchiveSearchToolDescriptions.QueryParameter)] string query,
        [Description(CharacterArchiveSearchToolDescriptions.LimitParameter)] int limit,
        CancellationToken cancellationToken)
    {
        var effectiveLimit = limit <= 0 ? DefaultLimit : Math.Min(limit, 20);
        var archiveSize = currentArchive.Characters.Count;
        CharacterBibleRunLogScope.Current?.Debug(
            "resolve.tool.search",
            $"candidateId={LogValueFormatter.ShortId(candidateId)} name={LogValueFormatter.Quote(candidateName)} query={LogValueFormatter.Quote(query)} limit={effectiveLimit} archiveSize={archiveSize}");
        var hits = await vectorSearchTool.SearchAsync(
            currentArchive,
            query,
            effectiveLimit,
            cancellationToken).ConfigureAwait(false);

        var searchHits = hits
            .Select((hit, index) => ToSearchHit(hit, index + 1))
            .ToArray();
        foreach (var hit in searchHits)
        {
            observedEntryIds.Add(hit.EntryId);
        }

        var result = new CharacterArchiveSearchResult(
            query,
            effectiveLimit,
            archiveSize,
            searchHits.Length,
            CharacterArchiveSearchResultNotes.ClosestEntriesMayBeUnrelated,
            searchHits);
        CharacterBibleLlmInputLogger.DebugInput(
            "resolve.tool.search.result",
            $"candidateId={LogValueFormatter.ShortId(candidateId)} returned={searchHits.Length} archiveSize={archiveSize} modelType={nameof(CharacterArchiveSearchResult)}",
            result);
        foreach (var hit in searchHits)
        {
            CharacterBibleRunLogScope.Current?.Debug(
                "resolve.search.hit",
                $"candidateId={LogValueFormatter.ShortId(candidateId)} rank={hit.Rank} entryId={hit.EntryId} name={LogValueFormatter.Quote(hit.Name)} gender={LogValueFormatter.Quote(hit.Gender)} aliases={LogValueFormatter.List(hit.Aliases)} score={LogValueFormatter.Score(hit.Score)} summary={LogValueFormatter.Quote(LogValueFormatter.ShortText(hit.Identity))}");
        }

        return result;
    }

    private static CharacterArchiveSearchHit ToSearchHit(
        CharacterVectorSearchHit hit,
        int rank)
    {
        return new CharacterArchiveSearchHit(
            rank,
            hit.Card.EntryId,
            hit.Card.Name,
            hit.Card.Gender,
            hit.Card.Aliases,
            hit.Card.Summary,
            hit.Score);
    }
}
