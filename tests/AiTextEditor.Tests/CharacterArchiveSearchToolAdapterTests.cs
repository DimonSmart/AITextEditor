using AiTextEditor.Agent.CharacterBible.Resolution;
using AiTextEditor.Agent.CharacterBible.VectorSearch;
using AiTextEditor.Core.Model;
using Xunit;

namespace AiTextEditor.Tests;

public sealed class CharacterArchiveSearchToolAdapterTests
{
    [Fact]
    public async Task SearchCharactersAsync_ReturnsResultMetadataAndRankedHits()
    {
        var archive = new CharacterDossiers(
            "test",
            1,
            [
                Character(1, "Незнайка", "male"),
                Character(2, "Знайка", "male")
            ]);
        var adapter = new CharacterArchiveSearchToolAdapter(
            archive,
            new FakeCharacterVectorSearchTool([
                Hit(2, "Знайка", "male", 0.75),
                Hit(1, "Незнайка", "male", 0.59)
            ]));

        var result = await adapter.SearchCharactersAsync("Фуксия", 10, CancellationToken.None);

        Assert.Equal("Фуксия", result.Query);
        Assert.Equal(10, result.Limit);
        Assert.Equal(2, result.ArchiveSize);
        Assert.Equal(2, result.ReturnedCount);
        Assert.Equal(CharacterArchiveSearchResultNotes.ClosestEntriesMayBeUnrelated, result.Note);
        Assert.Equal([1, 2], result.Hits.Select(hit => hit.Rank));
        Assert.Equal([2, 1], result.Hits.Select(hit => hit.CharacterId));
    }

    [Fact]
    public async Task SearchCharactersAsync_NormalizesLimitForResultAndVectorSearch()
    {
        var archive = new CharacterDossiers("test", 1, [Character(1, "Незнайка", "male")]);
        var vectorSearchTool = new FakeCharacterVectorSearchTool([]);
        var adapter = new CharacterArchiveSearchToolAdapter(archive, vectorSearchTool);

        var defaultLimitResult = await adapter.SearchCharactersAsync("Незнайка", 0, CancellationToken.None);
        var cappedLimitResult = await adapter.SearchCharactersAsync("Незнайка", 50, CancellationToken.None);

        Assert.Equal(5, defaultLimitResult.Limit);
        Assert.Equal(5, vectorSearchTool.RequestedLimits[0]);
        Assert.Equal(20, cappedLimitResult.Limit);
        Assert.Equal(20, vectorSearchTool.RequestedLimits[1]);
    }

    [Fact]
    public async Task SearchCharactersAsync_WhenArchiveIsEmpty_ReturnsNewCandidateMetadataWithoutVectorSearch()
    {
        var archive = new CharacterDossiers("test", 1, [], 1);
        var vectorSearchTool = new ThrowingCharacterVectorSearchTool();
        var adapter = new CharacterArchiveSearchToolAdapter(archive, vectorSearchTool);

        var result = await adapter.SearchCharactersAsync("Незнайка", 5, CancellationToken.None);

        Assert.Equal("Незнайка", result.Query);
        Assert.Equal(5, result.Limit);
        Assert.Equal(0, result.ArchiveSize);
        Assert.Equal(0, result.ReturnedCount);
        Assert.Equal(CharacterArchiveSearchResultNotes.EmptyArchive, result.Note);
        Assert.Empty(result.Hits);
    }

    private static CharacterDossier Character(
        int characterId,
        string name,
        string gender)
    {
        return new CharacterDossier(
            characterId,
            name,
            [],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            gender);
    }

    private static CharacterVectorSearchHit Hit(
        int characterId,
        string name,
        string gender,
        double score)
    {
        return new CharacterVectorSearchHit(
            new CharacterVectorSearchCard(
                characterId,
                name,
                gender,
                [],
                $"Name: {name} Gender: {gender}"),
            score);
    }

    private sealed class FakeCharacterVectorSearchTool(
        IReadOnlyList<CharacterVectorSearchHit> hits) : ICharacterVectorSearchTool
    {
        public List<int> RequestedLimits { get; } = [];

        public Task<IReadOnlyList<CharacterVectorSearchHit>> SearchAsync(
            CharacterDossiers dossiers,
            string query,
            int limit,
            CancellationToken cancellationToken)
        {
            RequestedLimits.Add(limit);
            return Task.FromResult<IReadOnlyList<CharacterVectorSearchHit>>(hits.Take(limit).ToArray());
        }
    }

    private sealed class ThrowingCharacterVectorSearchTool : ICharacterVectorSearchTool
    {
        public Task<IReadOnlyList<CharacterVectorSearchHit>> SearchAsync(
            CharacterDossiers dossiers,
            string query,
            int limit,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Vector search should not be called for an empty archive.");
        }
    }
}
