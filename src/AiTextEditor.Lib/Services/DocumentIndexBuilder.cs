using AiTextEditor.Lib.Model;
using AiTextEditor.Lib.Model.Indexing;
using System.Linq;

namespace AiTextEditor.Lib.Services;

/// <summary>
/// Builds text and structural indexes over a parsed <see cref="Document"/>.
/// </summary>
public class DocumentIndexBuilder
{
    public DocumentIndexes Build(Document document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var textIndex = BuildTextIndex(document);
        var structuralIndex = BuildStructuralIndex(document);

        return new DocumentIndexes
        {
            TextIndex = textIndex,
            StructuralIndex = structuralIndex
        };
    }

    public TextIndex BuildTextIndex(Document document)
    {
        var index = new TextIndex
        {
            DocumentId = document.Id
        };

        foreach (var block in document.Blocks)
        {
            index.Entries.Add(new TextIndexEntry
            {
                BlockId = block.Id,
                Text = string.IsNullOrWhiteSpace(block.PlainText) ? block.Markdown : block.PlainText,
                StartOffset = block.StartOffset,
                EndOffset = block.EndOffset,
                StartLine = block.StartLine,
                EndLine = block.EndLine,
                StructuralPath = block.StructuralPath,
                BlockType = block.Type
            });
        }

        return index;
    }

    public StructuralIndex BuildStructuralIndex(Document document)
    {
        var index = new StructuralIndex
        {
            DocumentId = document.Id
        };

        foreach (var block in document.Blocks.Where(b => b.Type == BlockType.Heading))
        {
            index.Headings.Add(new StructuralIndexEntry
            {
                BlockId = block.Id,
                Title = block.PlainText,
                Numbering = block.Numbering ?? string.Empty,
                Level = block.Level,
                StartOffset = block.StartOffset,
                EndOffset = block.EndOffset,
                StartLine = block.StartLine,
                EndLine = block.EndLine,
                StructuralPath = block.StructuralPath,
                HeadingPath = block.HeadingPath
            });
        }

        // Auxiliary entities (figures/listings) can be derived later; keep list empty for now.
        return index;
    }
}
