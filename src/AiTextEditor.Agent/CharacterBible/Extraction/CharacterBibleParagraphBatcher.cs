using System.Text;
using AiTextEditor.Agent.CharacterBible;

namespace AiTextEditor.Agent.CharacterBible.Extraction;

internal sealed class CharacterBibleParagraphBatcher
{
    private readonly CharacterBibleExtractionLimits limits;

    public CharacterBibleParagraphBatcher(CharacterBibleExtractionLimits limits)
    {
        this.limits = limits ?? throw new ArgumentNullException(nameof(limits));
    }

    public IEnumerable<List<(string Pointer, string Text)>> SplitParagraphs(
        IReadOnlyList<(string Pointer, string Text)> paragraphs)
    {
        if (paragraphs.Count == 0)
        {
            yield break;
        }

        var maxElements = Math.Max(1, limits.MaxParagraphsPerBatch);
        var maxBytes = Math.Max(1, limits.MaxBatchBytes);

        var startIndex = 0;
        while (startIndex < paragraphs.Count)
        {
            var batch = new List<(string Pointer, string Text)>(Math.Min(maxElements, paragraphs.Count - startIndex));
            var batchBytes = 0;
            var nextIndex = startIndex;

            while (nextIndex < paragraphs.Count)
            {
                var paragraph = paragraphs[nextIndex];
                var text = paragraph.Text ?? string.Empty;
                var size = Encoding.UTF8.GetByteCount(text);
                var wouldOverflow = batch.Count >= maxElements || batchBytes + size > maxBytes;

                if (wouldOverflow && batch.Count > 0)
                {
                    break;
                }

                batch.Add(paragraph);
                batchBytes += size;
                nextIndex++;

                if (batchBytes >= maxBytes)
                {
                    break;
                }
            }

            if (batch.Count == 0)
            {
                batch.Add(paragraphs[startIndex]);
                nextIndex = startIndex + 1;
            }

            yield return batch;

            if (nextIndex >= paragraphs.Count)
            {
                yield break;
            }

            var overlap = Math.Min(Math.Max(0, limits.OverlapParagraphs), Math.Max(0, batch.Count - 1));
            startIndex = nextIndex - overlap;
        }
    }
}
