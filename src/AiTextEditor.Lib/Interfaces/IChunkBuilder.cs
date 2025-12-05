using AiTextEditor.Lib.Model;

namespace AiTextEditor.Lib.Interfaces;

public interface IChunkBuilder
{
    List<Chunk> BuildChunks(Document document, int maxTokensApprox);
}
