using AiTextEditor.Lib.Interfaces;
using AiTextEditor.Lib.Model;
using System.Text;

namespace AiTextEditor.Lib.Services;

public class ChunkBuilder : IChunkBuilder
{
    public List<Chunk> BuildChunks(Document document, int maxTokensApprox)
    {
        var chunks = new List<Chunk>();
        var currentChunkBlocks = new List<Block>();
        var currentChunkText = new StringBuilder();
        
        // Stack to track headings: Level -> Text
        // We need to maintain the path. 
        // A simple way is to store the current active heading for each level.
        var headings = new Dictionary<int, string>();

        foreach (var block in document.Blocks)
        {
            // Update heading path if this is a heading
            if (block.Type == BlockType.Heading)
            {
                headings[block.Level] = block.PlainText;
                // Clear deeper levels
                var levels = headings.Keys.ToList();
                foreach (var level in levels)
                {
                    if (level > block.Level)
                    {
                        headings.Remove(level);
                    }
                }
            }

            // Estimate tokens for this block
            // Simple estimation: 1 token ~= 4 chars
            int blockTokens = block.Markdown.Length / 4;
            int currentChunkTokens = currentChunkText.Length / 4;

            // If adding this block exceeds max tokens AND we have something in the chunk, start a new one.
            // If the block itself is huge, we still add it (requirement: never split a block).
            if (currentChunkBlocks.Count > 0 && (currentChunkTokens + blockTokens > maxTokensApprox))
            {
                chunks.Add(CreateChunk(currentChunkBlocks, headings));
                currentChunkBlocks.Clear();
                currentChunkText.Clear();
            }

            currentChunkBlocks.Add(block);
            currentChunkText.Append(block.Markdown);
            currentChunkText.AppendLine();
        }

        if (currentChunkBlocks.Count > 0)
        {
            chunks.Add(CreateChunk(currentChunkBlocks, headings));
        }

        return chunks;
    }

    private Chunk CreateChunk(List<Block> blocks, Dictionary<int, string> headings)
    {
        var sb = new StringBuilder();
        foreach (var b in blocks)
        {
            sb.AppendLine(b.Markdown);
        }

        // Build heading path string
        var sortedLevels = headings.Keys.OrderBy(k => k);
        var pathParts = new List<string>();
        foreach (var level in sortedLevels)
        {
            pathParts.Add(headings[level]);
        }
        var headingPath = string.Join(" > ", pathParts);

        return new Chunk
        {
            Id = Guid.NewGuid().ToString(),
            BlockIds = blocks.Select(b => b.Id).ToList(),
            Markdown = sb.ToString(),
            HeadingPath = headingPath
        };
    }
}
