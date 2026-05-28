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
    private readonly HashSet<string> observedEntryIds = new(StringComparer.Ordinal);

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

    public IReadOnlySet<string> ObservedEntryIds => observedEntryIds;

    [Description("Search character archive by a plain text query and return compact candidate identity cards.")]
    public async Task<IReadOnlyList<CharacterArchiveSearchHit>> SearchCharactersAsync(
        [Description("Plain text query built from candidate name, aliases, and useful context words.")] string query,
        [Description("Maximum number of character cards to return.")] int limit,
        CancellationToken cancellationToken)
    {
        var effectiveLimit = limit <= 0 ? DefaultLimit : Math.Min(limit, 20);
        CharacterBibleRunLogScope.Current?.Debug(
            "resolve.search",
            $"candidateId={LogValueFormatter.ShortId(candidateId)} name={LogValueFormatter.Quote(candidateName)} query={LogValueFormatter.Quote(query)} limit={effectiveLimit}");
        var hits = await vectorSearchTool.SearchAsync(
            currentArchive,
            query,
            effectiveLimit,
            cancellationToken).ConfigureAwait(false);

        var searchHits = hits.Select(ToSearchHit).ToArray();
        foreach (var hit in searchHits)
        {
            if (!string.IsNullOrWhiteSpace(hit.EntryId))
            {
                observedEntryIds.Add(hit.EntryId.Trim());
            }
        }

        CharacterBibleRunLogScope.Current?.Debug(
            "resolve.search.hits",
            $"candidateId={LogValueFormatter.ShortId(candidateId)} count={searchHits.Length} hits={LogValueFormatter.Hits(searchHits)}");
        for (var index = 0; index < searchHits.Length; index++)
        {
            var hit = searchHits[index];
            CharacterBibleRunLogScope.Current?.Debug(
                "resolve.search.hit",
                $"candidateId={LogValueFormatter.ShortId(candidateId)} rank={index + 1} entryId={hit.EntryId} name={LogValueFormatter.Quote(hit.Name)} gender={LogValueFormatter.Quote(hit.Gender)} aliases={LogValueFormatter.List(hit.Aliases)} score={LogValueFormatter.Score(hit.Score)} summary={LogValueFormatter.Quote(LogValueFormatter.ShortText(hit.Identity))}");
        }

        return searchHits;
    }

    private static CharacterArchiveSearchHit ToSearchHit(CharacterVectorSearchHit hit)
    {
        return new CharacterArchiveSearchHit(
            hit.Card.EntryId,
            hit.Card.Name,
            hit.Card.Gender,
            hit.Card.Aliases,
            hit.Card.Summary,
            hit.Score);
    }
}
