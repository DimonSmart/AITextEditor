using AiTextEditor.Agent.CharacterBible.VectorSearch;
using AiTextEditor.Core.Model;
using AiTextEditor.Core.Services;
using Xunit;

namespace AiTextEditor.Tests;

public sealed class CharacterVectorSearchToolTests
{
    [Fact]
    public async Task SearchAsync_SemanticQueryFindsCharacterByDescriptionWithoutName()
    {
        var dossierService = new CharacterDossierService();
        dossierService.UpsertDossier(Character(
            "ponchik",
            "Пончик",
            appearance: "полный коротышка",
            traits: "любит поесть, тревожится в опасных ситуациях"));
        dossierService.UpsertDossier(Character(
            "znaika",
            "Знайка",
            status: "ученый коротышка",
            traits: "рассудительный и компетентный"));

        var embeddings = new FakeCharacterVectorEmbeddingClient(text =>
        {
            if (text.Contains("толстый коротышка", StringComparison.OrdinalIgnoreCase))
            {
                return [1, 0];
            }

            if (text.Contains("любит поесть", StringComparison.OrdinalIgnoreCase))
            {
                return [1, 0];
            }

            return [0, 1];
        });
        var tool = CreateTool(embeddings);

        var result = await tool.SearchAsync(dossierService.GetDossiers(), "толстый коротышка, любит есть, иногда трусит", 2, CancellationToken.None);

        Assert.Equal("ponchik", result[0].Card.EntryId);
        Assert.DoesNotContain("Пончик", "толстый коротышка, любит есть, иногда трусит", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SearchAsync_EmptyQueryOrNonPositiveLimit_ReturnsEmpty()
    {
        var dossierService = new CharacterDossierService();
        dossierService.UpsertDossier(Character("c1", "John"));
        var tool = CreateTool(new FakeCharacterVectorEmbeddingClient(_ => [1]));

        Assert.Empty(await tool.SearchAsync(dossierService.GetDossiers(), "", 5, CancellationToken.None));
        Assert.Empty(await tool.SearchAsync(dossierService.GetDossiers(), "   ", 5, CancellationToken.None));
        Assert.Empty(await tool.SearchAsync(dossierService.GetDossiers(), "John", 0, CancellationToken.None));
        Assert.Empty(await tool.SearchAsync(dossierService.GetDossiers(), "John", -1, CancellationToken.None));
    }

    [Fact]
    public async Task SearchAsync_LimitIsRespected()
    {
        var dossierService = new CharacterDossierService();
        dossierService.UpsertDossier(Character("c1", "A"));
        dossierService.UpsertDossier(Character("c2", "B"));
        dossierService.UpsertDossier(Character("c3", "C"));
        var tool = CreateTool(new FakeCharacterVectorEmbeddingClient(_ => [1]));

        var result = await tool.SearchAsync(dossierService.GetDossiers(), "anything", 2, CancellationToken.None);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task SearchAsync_ResultsSortedByVectorScore()
    {
        var dossierService = new CharacterDossierService();
        dossierService.UpsertDossier(Character("weak", "Weak", traits: "weak-vector"));
        dossierService.UpsertDossier(Character("strong", "Strong", traits: "strong-vector"));
        var tool = CreateTool(new FakeCharacterVectorEmbeddingClient(text =>
        {
            if (text.Contains("query", StringComparison.OrdinalIgnoreCase))
            {
                return [1, 0];
            }

            if (text.Contains("strong-vector", StringComparison.OrdinalIgnoreCase))
            {
                return [1, 0];
            }

            return [0, 1];
        }));

        var result = await tool.SearchAsync(dossierService.GetDossiers(), "query", 2, CancellationToken.None);

        Assert.Equal("strong", result[0].Card.EntryId);
        Assert.True(result[0].Score > result[1].Score);
    }

    [Fact]
    public async Task SearchAsync_EqualScoresSortedByNameThenEntryId()
    {
        var dossierService = new CharacterDossierService();
        dossierService.UpsertDossier(Character("b2", "Bob"));
        dossierService.UpsertDossier(Character("a2", "Alice"));
        dossierService.UpsertDossier(Character("a1", "Alice"));
        var tool = CreateTool(new FakeCharacterVectorEmbeddingClient(_ => [1, 0]));

        var result = await tool.SearchAsync(dossierService.GetDossiers(), "same", 3, CancellationToken.None);

        Assert.Equal(["a1", "a2", "b2"], result.Select(hit => hit.Card.EntryId).ToArray());
    }

    [Fact]
    public async Task SearchAsync_NewCharacterAppearsAfterNextSearch()
    {
        var dossierService = new CharacterDossierService();
        dossierService.UpsertDossier(Character("c1", "First"));
        var tool = CreateTool(new FakeCharacterVectorEmbeddingClient(text =>
            text.Contains("second", StringComparison.OrdinalIgnoreCase) ? [1, 0] : [0, 1]));

        await tool.SearchAsync(dossierService.GetDossiers(), "second", 5, CancellationToken.None);
        dossierService.UpsertDossier(Character("c2", "Second", traits: "second"));

        var result = await tool.SearchAsync(dossierService.GetDossiers(), "second", 5, CancellationToken.None);

        Assert.Contains(result, hit => hit.Card.EntryId == "c2");
        Assert.Equal("c2", result[0].Card.EntryId);
    }

    [Fact]
    public async Task SearchAsync_ChangedCharacterIsReembeddedAfterNextSearch()
    {
        var dossierService = new CharacterDossierService();
        dossierService.UpsertDossier(Character("c1", "First", traits: "old"));
        var embeddings = new FakeCharacterVectorEmbeddingClient(_ => [1, 0]);
        var tool = CreateTool(embeddings);

        await tool.SearchAsync(dossierService.GetDossiers(), "query", 1, CancellationToken.None);
        var callsAfterFirstSearch = embeddings.DocumentEmbeddingCallCount;

        dossierService.UpsertDossier(Character("c1", "First", traits: "new semantic detail"));
        await tool.SearchAsync(dossierService.GetDossiers(), "query", 1, CancellationToken.None);

        Assert.Equal(callsAfterFirstSearch + 1, embeddings.DocumentEmbeddingCallCount);
    }

    [Fact]
    public async Task SearchAsync_UnchangedCharacterIsNotReembeddedAgain()
    {
        var dossierService = new CharacterDossierService();
        dossierService.UpsertDossier(Character("c1", "First", traits: "same"));
        var embeddings = new FakeCharacterVectorEmbeddingClient(_ => [1, 0]);
        var tool = CreateTool(embeddings);

        await tool.SearchAsync(dossierService.GetDossiers(), "query", 1, CancellationToken.None);
        var callsAfterFirstSearch = embeddings.DocumentEmbeddingCallCount;
        await tool.SearchAsync(dossierService.GetDossiers(), "query", 1, CancellationToken.None);

        Assert.Equal(callsAfterFirstSearch, embeddings.DocumentEmbeddingCallCount);
    }

    [Fact]
    public async Task SearchAsync_RemovedCharacterDisappearsAfterNextSearch()
    {
        var dossierService = new CharacterDossierService();
        dossierService.UpsertDossier(Character("c1", "First"));
        dossierService.UpsertDossier(Character("c2", "Second"));
        var tool = CreateTool(new FakeCharacterVectorEmbeddingClient(_ => [1, 0]));

        await tool.SearchAsync(dossierService.GetDossiers(), "query", 5, CancellationToken.None);
        dossierService.RemoveDossier("c2");

        var result = await tool.SearchAsync(dossierService.GetDossiers(), "query", 5, CancellationToken.None);

        Assert.DoesNotContain(result, hit => hit.Card.EntryId == "c2");
        Assert.Contains(result, hit => hit.Card.EntryId == "c1");
    }

    [Fact]
    public void Fingerprint_ChangesWhenIndexSchemaVersionChanges()
    {
        var dossier = Character("c1", "First", traits: "same");
        var firstTool = CreateTool(new FakeCharacterVectorEmbeddingClient(_ => [1]), "model", "schema-1");
        var secondTool = CreateTool(new FakeCharacterVectorEmbeddingClient(_ => [1]), "model", "schema-2");

        Assert.NotEqual(
            firstTool.GetFingerprintForTests(dossier),
            secondTool.GetFingerprintForTests(dossier));
    }

    [Fact]
    public void Fingerprint_ChangesWhenEmbeddingModelIdChanges()
    {
        var dossier = Character("c1", "First", traits: "same");
        var firstTool = CreateTool(new FakeCharacterVectorEmbeddingClient(_ => [1]), "model-a");
        var secondTool = CreateTool(new FakeCharacterVectorEmbeddingClient(_ => [1]), "model-b");

        Assert.NotEqual(
            firstTool.GetFingerprintForTests(dossier),
            secondTool.GetFingerprintForTests(dossier));
    }

    [Fact]
    public async Task SearchAsync_ReturnedCardDoesNotExposeDossierOrEmbedding()
    {
        var dossierService = new CharacterDossierService();
        dossierService.UpsertDossier(Character("c1", "First"));
        var tool = CreateTool(new FakeCharacterVectorEmbeddingClient(_ => [1]));

        var result = await tool.SearchAsync(dossierService.GetDossiers(), "query", 1, CancellationToken.None);
        var card = result[0].Card;

        Assert.Equal(["Aliases", "EntryId", "Gender", "Name", "Summary"], PropertyNames(card));
        Assert.Equal(["Card", "Score"], PropertyNames(result[0]));
    }

    [Fact]
    public async Task SearchAsync_NameOnlyExactMatchIsNotSpeciallyBoosted()
    {
        var dossierService = new CharacterDossierService();
        dossierService.UpsertDossier(Character("exact", "Pony", traits: "low-vector"));
        dossierService.UpsertDossier(Character("semantic", "Other", traits: "high-vector"));
        var tool = CreateTool(new FakeCharacterVectorEmbeddingClient(text =>
        {
            if (string.Equals(text, "Pony", StringComparison.Ordinal))
            {
                return [1, 0];
            }

            if (text.Contains("high-vector", StringComparison.OrdinalIgnoreCase))
            {
                return [1, 0];
            }

            return [0, 1];
        }));

        var result = await tool.SearchAsync(dossierService.GetDossiers(), "Pony", 2, CancellationToken.None);

        Assert.Equal("semantic", result[0].Card.EntryId);
    }

    private static CharacterVectorSearchTool CreateTool(
        FakeCharacterVectorEmbeddingClient embeddings,
        string embeddingModelId = "test-embedding-model",
        string schemaVersion = CharacterVectorSearchOptions.CurrentIndexSchemaVersion)
    {
        return new CharacterVectorSearchTool(
            embeddings,
            new CharacterVectorSearchOptions(embeddingModelId, schemaVersion));
    }

    private static CharacterDossier Character(
        string characterId,
        string name,
        string gender = "unknown",
        IReadOnlyDictionary<string, string>? aliasExamples = null,
        string appearance = "",
        string status = "",
        string traits = "",
        string speech = "")
    {
        var aliases = aliasExamples?.Keys.ToArray() ?? [];
        return new CharacterDossier(
            characterId,
            name,
            aliases,
            aliasExamples ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            gender,
            Profile: new CharacterProfile(
                appearance,
                status,
                traits,
                speech));
    }

    private static string[] PropertyNames<T>(T value)
    {
        Assert.NotNull(value);
        return value!.GetType()
            .GetProperties()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
    }

    private sealed class FakeCharacterVectorEmbeddingClient(
        Func<string, float[]> embed) : ICharacterVectorEmbeddingClient
    {
        public int DocumentEmbeddingCallCount { get; private set; }

        public Task<ReadOnlyMemory<float>> GenerateEmbeddingAsync(
            string text,
            CancellationToken cancellationToken)
        {
            if (text.Contains("Character:", StringComparison.Ordinal))
            {
                DocumentEmbeddingCallCount++;
            }

            return Task.FromResult<ReadOnlyMemory<float>>(embed(text));
        }
    }
}

