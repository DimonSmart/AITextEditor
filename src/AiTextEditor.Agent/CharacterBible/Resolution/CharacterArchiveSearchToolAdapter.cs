using System.ComponentModel;
using AiTextEditor.Core.Model;

namespace AiTextEditor.Agent.CharacterBible.Resolution;

internal sealed class CharacterArchiveSearchToolAdapter : ICharacterArchiveSearchTool
{
    private const int DefaultLimit = 5;

    private readonly CharacterDossiers dossiers;
    private readonly CharacterArchiveSearchService searchService;

    public CharacterArchiveSearchToolAdapter(
        CharacterDossiers dossiers,
        CharacterArchiveSearchService searchService)
    {
        this.dossiers = dossiers ?? throw new ArgumentNullException(nameof(dossiers));
        this.searchService = searchService ?? throw new ArgumentNullException(nameof(searchService));
    }

    [Description("Search character archive by a plain text query and return compact candidate identity cards.")]
    public Task<IReadOnlyList<CharacterArchiveSearchHit>> SearchCharactersAsync(
        [Description("Plain text query built from candidate name, aliases, and useful context words.")] string query,
        [Description("Maximum number of character cards to return.")] int limit,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var effectiveLimit = limit <= 0 ? DefaultLimit : Math.Min(limit, 20);
        return Task.FromResult(searchService.SearchCharacters(dossiers, query, effectiveLimit));
    }
}
