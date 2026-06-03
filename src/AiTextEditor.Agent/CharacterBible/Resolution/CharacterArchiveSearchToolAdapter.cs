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
    private readonly int? candidateIndex;
    private readonly string? candidateName;
    private readonly HashSet<int> observedCharacterIds = [];

    public CharacterArchiveSearchToolAdapter(
        CharacterDossiers currentArchive,
        ICharacterVectorSearchTool vectorSearchTool,
        int? candidateIndex = null,
        string? candidateName = null)
    {
        this.currentArchive = currentArchive ?? throw new ArgumentNullException(nameof(currentArchive));
        this.vectorSearchTool = vectorSearchTool ?? throw new ArgumentNullException(nameof(vectorSearchTool));
        this.candidateIndex = candidateIndex;
        this.candidateName = candidateName;
    }

    public IReadOnlySet<int> ObservedCharacterIds => observedCharacterIds;

    internal int ArchiveSize => currentArchive.Characters.Count;

    internal int? CandidateIndex => candidateIndex;

    internal string? CandidateName => candidateName;

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
            $"candidateIndex={candidateIndex} name={LogValueFormatter.Quote(candidateName)} query={LogValueFormatter.Quote(query)} limit={effectiveLimit} archiveSize={archiveSize}");
        if (archiveSize == 0)
        {
            CharacterBibleRunLogScope.Current?.Info(
                "resolve.fast_path.search_empty_archive",
                $"candidateIndex={candidateIndex} name={LogValueFormatter.Quote(candidateName)} decision=new returned=0 archiveSize=0");
            var emptyResult = new CharacterArchiveSearchResult(
                query,
                effectiveLimit,
                archiveSize,
                0,
                CharacterArchiveSearchResultNotes.EmptyArchive,
                []);
            CharacterBibleLlmInputLogger.DebugInput(
                "resolve.tool.search.result",
                $"candidateIndex={candidateIndex} name={LogValueFormatter.Quote(candidateName)} returned=0 archiveSize=0 modelType={nameof(CharacterArchiveSearchResult)}",
                emptyResult);
            return emptyResult;
        }

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
            observedCharacterIds.Add(hit.CharacterId);
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
            $"candidateIndex={candidateIndex} name={LogValueFormatter.Quote(candidateName)} returned={searchHits.Length} archiveSize={archiveSize} modelType={nameof(CharacterArchiveSearchResult)}",
            result);
        foreach (var hit in searchHits)
        {
            CharacterBibleRunLogScope.Current?.Debug(
                "resolve.search.hit",
                $"candidateIndex={candidateIndex} rank={hit.Rank} characterId={hit.CharacterId} name={LogValueFormatter.Quote(hit.Name)} gender={LogValueFormatter.Quote(hit.Gender)} observedNameForms={LogValueFormatter.List(hit.ObservedNameForms)} score={LogValueFormatter.Score(hit.Score)} summary={LogValueFormatter.Quote(LogValueFormatter.ShortText(hit.Identity))}");
        }

        return result;
    }

    private static CharacterArchiveSearchHit ToSearchHit(
        CharacterVectorSearchHit hit,
        int rank)
    {
        return new CharacterArchiveSearchHit(
            rank,
            hit.Card.CharacterId,
            hit.Card.Name,
            hit.Card.Gender,
            hit.Card.ObservedNameForms,
            hit.Card.Summary,
            hit.Score);
    }
}
