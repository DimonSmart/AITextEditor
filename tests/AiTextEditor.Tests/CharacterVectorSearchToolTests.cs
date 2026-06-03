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
            1,
            "Пончик",
            appearance: "полный коротышка",
            traits: "любит поесть, тревожится в опасных ситуациях"));
        dossierService.UpsertDossier(Character(
            2,
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

        Assert.Equal(1, result[0].Card.CharacterId);
        Assert.DoesNotContain("Пончик", "толстый коротышка, любит есть, иногда трусит", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SearchAsync_EmptyQueryOrNonPositiveLimit_ReturnsEmpty()
    {
        var dossierService = new CharacterDossierService();
        dossierService.UpsertDossier(Character(1, "John"));
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
        dossierService.UpsertDossier(Character(1, "A"));
        dossierService.UpsertDossier(Character(2, "B"));
        dossierService.UpsertDossier(Character(3, "C"));
        var tool = CreateTool(new FakeCharacterVectorEmbeddingClient(_ => [1]));

        var result = await tool.SearchAsync(dossierService.GetDossiers(), "anything", 2, CancellationToken.None);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task SearchAsync_ResultsSortedByVectorScore()
    {
        var dossierService = new CharacterDossierService();
        dossierService.UpsertDossier(Character(1, "Weak", traits: "weak-vector"));
        dossierService.UpsertDossier(Character(2, "Strong", traits: "strong-vector"));
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

        Assert.Equal(2, result[0].Card.CharacterId);
        Assert.True(result[0].Score > result[1].Score);
    }

    [Fact]
    public async Task SearchAsync_EqualScoresSortedByNameThenCharacterId()
    {
        var dossierService = new CharacterDossierService();
        dossierService.UpsertDossier(Character(3, "Bob"));
        dossierService.UpsertDossier(Character(2, "Alice"));
        dossierService.UpsertDossier(Character(1, "Alice"));
        var tool = CreateTool(new FakeCharacterVectorEmbeddingClient(_ => [1, 0]));

        var result = await tool.SearchAsync(dossierService.GetDossiers(), "same", 3, CancellationToken.None);

        Assert.Equal([1, 2, 3], result.Select(hit => hit.Card.CharacterId).ToArray());
    }

    [Fact]
    public async Task SearchAsync_NewCharacterAppearsAfterNextSearch()
    {
        var dossierService = new CharacterDossierService();
        dossierService.UpsertDossier(Character(1, "First"));
        var tool = CreateTool(new FakeCharacterVectorEmbeddingClient(text =>
            text.Contains("second", StringComparison.OrdinalIgnoreCase) ? [1, 0] : [0, 1]));

        await tool.SearchAsync(dossierService.GetDossiers(), "second", 5, CancellationToken.None);
        dossierService.UpsertDossier(Character(2, "Second", traits: "second"));

        var result = await tool.SearchAsync(dossierService.GetDossiers(), "second", 5, CancellationToken.None);

        Assert.Contains(result, hit => hit.Card.CharacterId == 2);
        Assert.Equal(2, result[0].Card.CharacterId);
    }

    [Fact]
    public async Task SearchAsync_ChangedCharacterIsReembeddedAfterNextSearch()
    {
        var dossierService = new CharacterDossierService();
        dossierService.UpsertDossier(Character(1, "First", traits: "old"));
        var embeddings = new FakeCharacterVectorEmbeddingClient(_ => [1, 0]);
        var tool = CreateTool(embeddings);

        await tool.SearchAsync(dossierService.GetDossiers(), "query", 1, CancellationToken.None);
        var callsAfterFirstSearch = embeddings.DocumentEmbeddingCallCount;

        dossierService.UpsertDossier(Character(1, "First", traits: "new semantic detail"));
        await tool.SearchAsync(dossierService.GetDossiers(), "query", 1, CancellationToken.None);

        Assert.Equal(callsAfterFirstSearch + 1, embeddings.DocumentEmbeddingCallCount);
    }

    [Fact]
    public async Task SearchAsync_UnchangedCharacterIsNotReembeddedAgain()
    {
        var dossierService = new CharacterDossierService();
        dossierService.UpsertDossier(Character(1, "First", traits: "same"));
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
        dossierService.UpsertDossier(Character(1, "First"));
        dossierService.UpsertDossier(Character(2, "Second"));
        var tool = CreateTool(new FakeCharacterVectorEmbeddingClient(_ => [1, 0]));

        await tool.SearchAsync(dossierService.GetDossiers(), "query", 5, CancellationToken.None);
        dossierService.RemoveDossier(2);

        var result = await tool.SearchAsync(dossierService.GetDossiers(), "query", 5, CancellationToken.None);

        Assert.DoesNotContain(result, hit => hit.Card.CharacterId == 2);
        Assert.Contains(result, hit => hit.Card.CharacterId == 1);
    }

    [Fact]
    public void Fingerprint_ChangesWhenIndexSchemaVersionChanges()
    {
        var dossier = Character(1, "First", traits: "same");
        var firstTool = CreateTool(new FakeCharacterVectorEmbeddingClient(_ => [1]), "model", "schema-1");
        var secondTool = CreateTool(new FakeCharacterVectorEmbeddingClient(_ => [1]), "model", "schema-2");

        Assert.NotEqual(
            firstTool.GetFingerprintForTests(dossier),
            secondTool.GetFingerprintForTests(dossier));
    }

    [Fact]
    public void Fingerprint_ChangesWhenEmbeddingModelIdChanges()
    {
        var dossier = Character(1, "First", traits: "same");
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
        dossierService.UpsertDossier(Character(1, "First"));
        var tool = CreateTool(new FakeCharacterVectorEmbeddingClient(_ => [1]));

        var result = await tool.SearchAsync(dossierService.GetDossiers(), "query", 1, CancellationToken.None);
        var card = result[0].Card;

        Assert.Equal(["CharacterId", "Gender", "Name", "ObservedNameForms", "Summary"], PropertyNames(card));
        Assert.Equal(["Card", "Score"], PropertyNames(result[0]));
    }

    [Fact]
    public async Task SearchAsync_NameOnlyExactMatchIsNotSpeciallyBoosted()
    {
        var dossierService = new CharacterDossierService();
        dossierService.UpsertDossier(Character(1, "Pony", traits: "low-vector"));
        dossierService.UpsertDossier(Character(2, "Other", traits: "high-vector"));
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

        Assert.Equal(2, result[0].Card.CharacterId);
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
        int characterId,
        string name,
        string gender = "unknown",
        IReadOnlyDictionary<string, string>? observedNameFormExamples = null,
        string appearance = "",
        string status = "",
        string traits = "",
        string speech = "")
    {
        var observedNameForms = observedNameFormExamples?.Keys.ToArray() ?? [];
        return new CharacterDossier(
            characterId,
            name,
            observedNameForms,
            observedNameFormExamples ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
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

