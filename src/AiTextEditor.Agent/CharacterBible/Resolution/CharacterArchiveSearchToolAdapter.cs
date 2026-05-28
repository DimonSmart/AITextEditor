using System.ComponentModel;
using AiTextEditor.Agent.CharacterBible.VectorSearch;
using AiTextEditor.Core.Model;

namespace AiTextEditor.Agent.CharacterBible.Resolution;

internal sealed class CharacterArchiveSearchToolAdapter : ICharacterArchiveSearchTool
{
    private const int DefaultLimit = 5;

    private readonly CharacterDossiers currentArchive;
    private readonly ICharacterVectorSearchTool vectorSearchTool;

    public CharacterArchiveSearchToolAdapter(
        CharacterDossiers currentArchive,
        ICharacterVectorSearchTool vectorSearchTool)
    {
        this.currentArchive = currentArchive ?? throw new ArgumentNullException(nameof(currentArchive));
        this.vectorSearchTool = vectorSearchTool ?? throw new ArgumentNullException(nameof(vectorSearchTool));
    }

    [Description("Search character archive by a plain text query and return compact candidate identity cards.")]
    public async Task<IReadOnlyList<CharacterArchiveSearchHit>> SearchCharactersAsync(
        [Description("Plain text query built from candidate name, aliases, and useful context words.")] string query,
        [Description("Maximum number of character cards to return.")] int limit,
        CancellationToken cancellationToken)
    {
        var effectiveLimit = limit <= 0 ? DefaultLimit : Math.Min(limit, 20);
        var hits = await vectorSearchTool.SearchAsync(
            currentArchive,
            query,
            effectiveLimit,
            cancellationToken).ConfigureAwait(false);

        return hits.Select(ToSearchHit).ToArray();
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
