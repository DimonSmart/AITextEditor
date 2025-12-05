using AiTextEditor.Lib.Model;
using AiTextEditor.Lib.Services;

namespace AiTextEditor.Tests;

public class VectorStoreTests
{
    [Fact]
    public async Task QueryAsync_ReturnsAllChunksAndRespectsMaxResults()
    {
        var store = new InMemoryVectorStore();
        var chunks = new[]
        {
            new Chunk { Id = "c1", Markdown = "A" },
            new Chunk { Id = "c2", Markdown = "B" },
            new Chunk { Id = "c3", Markdown = "C" }
        };

        await store.IndexAsync("doc1", chunks);

        var all = await store.QueryAsync("doc1", "any query", maxResults: 5);
        Assert.Equal(3, all.Count);
        Assert.Equal(new[] { "c1", "c2", "c3" }, all.Select(c => c.Id));

        var top2 = await store.QueryAsync("doc1", "any query", maxResults: 2);
        Assert.Equal(2, top2.Count);
        Assert.Equal(new[] { "c1", "c2" }, top2.Select(c => c.Id));
    }

    [Fact]
    public async Task QueryAsync_WhenDocumentNotIndexed_ReturnsEmpty()
    {
        var store = new InMemoryVectorStore();

        var results = await store.QueryAsync("missing", "anything");

        Assert.Empty(results);
    }

    [Fact]
    public async Task IndexAsync_ReplacesExistingChunks()
    {
        var store = new InMemoryVectorStore();
        var initial = new[] { new Chunk { Id = "c1", Markdown = "Old" } };
        var updated = new[] { new Chunk { Id = "c2", Markdown = "New" } };

        await store.IndexAsync("doc1", initial);
        await store.IndexAsync("doc1", updated);

        var results = await store.QueryAsync("doc1", "query", maxResults: 5);

        Assert.Equal(new[] { "c2" }, results.Select(c => c.Id));
    }
}
