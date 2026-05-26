using AiTextEditor.Core.Interfaces;
using AiTextEditor.Core.Model;

namespace AiTextEditor.Agent.CharacterBible.Patching;

internal sealed class CharacterBibleEvidenceContextExpander
{
    private const int NearbyParagraphWindow = 1;

    private readonly IReadOnlyList<LinearItem> documentItems;
    private readonly Dictionary<string, int> pointerIndexes;

    public CharacterBibleEvidenceContextExpander(IDocumentContext documentContext)
    {
        ArgumentNullException.ThrowIfNull(documentContext);

        documentItems = documentContext.Document.Items;
        pointerIndexes = documentItems
            .Select((item, index) => new { Pointer = item.Pointer.ToCompactString(), Index = index })
            .GroupBy(item => item.Pointer, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Index, StringComparer.Ordinal);
    }

    public IReadOnlyList<CharacterBibleEvidenceContext> Expand(
        IReadOnlyList<CharacterBibleCandidateEvidence> evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        return evidence
            .Where(item => !string.IsNullOrWhiteSpace(item.Pointer) && !string.IsNullOrWhiteSpace(item.Excerpt))
            .Select(Expand)
            .DistinctBy(context => $"{context.Pointer}\u001f{context.AnchorExcerpt}", StringComparer.Ordinal)
            .ToArray();
    }

    private CharacterBibleEvidenceContext Expand(CharacterBibleCandidateEvidence evidence)
    {
        var pointer = evidence.Pointer.Trim();
        var anchorExcerpt = evidence.Excerpt.Trim();
        if (!pointerIndexes.TryGetValue(pointer, out var index))
        {
            return new CharacterBibleEvidenceContext(
                pointer,
                anchorExcerpt,
                anchorExcerpt,
                anchorExcerpt,
                []);
        }

        var currentParagraph = documentItems[index].Markdown.Trim();
        return new CharacterBibleEvidenceContext(
            pointer,
            anchorExcerpt,
            currentParagraph,
            BuildFocusedText(currentParagraph, anchorExcerpt),
            CollectNearbyParagraphs(index));
    }

    private IReadOnlyList<CharacterBibleNearbyParagraph> CollectNearbyParagraphs(int index)
    {
        var nearby = new List<CharacterBibleNearbyParagraph>(NearbyParagraphWindow * 2);
        AddNearbyParagraphs(nearby, index, -1, "previous");
        AddNearbyParagraphs(nearby, index, 1, "next");
        return nearby;
    }

    private void AddNearbyParagraphs(
        List<CharacterBibleNearbyParagraph> nearby,
        int sourceIndex,
        int direction,
        string position)
    {
        var added = 0;
        for (var index = sourceIndex + direction;
             index >= 0 && index < documentItems.Count && added < NearbyParagraphWindow;
             index += direction)
        {
            var item = documentItems[index];
            if (item.Type == LinearItemType.Heading || string.IsNullOrWhiteSpace(item.Markdown))
            {
                continue;
            }

            nearby.Add(new CharacterBibleNearbyParagraph(
                item.Pointer.ToCompactString(),
                item.Markdown.Trim(),
                position));
            added++;
        }
    }

    private static string BuildFocusedText(string currentParagraph, string anchorExcerpt)
    {
        if (string.IsNullOrWhiteSpace(currentParagraph))
        {
            return anchorExcerpt.Trim();
        }

        if (string.IsNullOrWhiteSpace(anchorExcerpt))
        {
            return currentParagraph.Trim();
        }

        var anchorIndex = currentParagraph.IndexOf(anchorExcerpt, StringComparison.OrdinalIgnoreCase);
        if (anchorIndex < 0)
        {
            return currentParagraph.Trim();
        }

        var spans = SplitSentenceSpans(currentParagraph);
        var sentenceIndex = spans.FindIndex(span => anchorIndex >= span.Start && anchorIndex < span.End);
        if (sentenceIndex < 0)
        {
            return currentParagraph.Trim();
        }

        var start = Math.Max(0, sentenceIndex - 1);
        var end = Math.Min(spans.Count - 1, sentenceIndex + 1);
        return string.Join(
            " ",
            spans
                .Skip(start)
                .Take(end - start + 1)
                .Select(span => currentParagraph[span.Start..span.End].Trim())
                .Where(text => text.Length > 0));
    }

    private static List<TextSpan> SplitSentenceSpans(string text)
    {
        var spans = new List<TextSpan>();
        var start = 0;

        for (var index = 0; index < text.Length; index++)
        {
            if (!IsSentenceBoundary(text[index]))
            {
                continue;
            }

            var end = index + 1;
            while (end < text.Length && char.IsWhiteSpace(text[end]))
            {
                end++;
            }

            AddSpan(start, end);
            start = end;
        }

        AddSpan(start, text.Length);
        return spans;

        void AddSpan(int spanStart, int spanEnd)
        {
            while (spanStart < spanEnd && char.IsWhiteSpace(text[spanStart]))
            {
                spanStart++;
            }

            while (spanEnd > spanStart && char.IsWhiteSpace(text[spanEnd - 1]))
            {
                spanEnd--;
            }

            if (spanEnd > spanStart)
            {
                spans.Add(new TextSpan(spanStart, spanEnd));
            }
        }
    }

    private static bool IsSentenceBoundary(char value)
        => value is '.' or '!' or '?' or '…';

    private readonly record struct TextSpan(int Start, int End);
}
