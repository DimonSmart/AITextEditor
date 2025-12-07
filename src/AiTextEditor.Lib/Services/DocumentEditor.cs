using System;
using System.Collections.Generic;
using System.Linq;
using AiTextEditor.Lib.Interfaces;
using AiTextEditor.Lib.Model;

namespace AiTextEditor.Lib.Services;

public class DocumentEditor : IDocumentEditor
{
    private readonly MarkdownDocumentRepository repository;

    public DocumentEditor()
        : this(new MarkdownDocumentRepository())
    {
    }

    public DocumentEditor(MarkdownDocumentRepository repository)
    {
        this.repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public void ApplyEdits(Document document, IEnumerable<EditOperation> operations)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(operations);

        var blocks = document.Blocks;

        foreach (var op in operations)
        {
            if (op.Action == EditActionType.Keep)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(op.TargetBlockId))
            {
                continue;
            }

            var index = blocks.FindIndex(b => b.Id == op.TargetBlockId);
            if (index < 0)
            {
                continue;
            }

            switch (op.Action)
            {
                case EditActionType.Replace when op.NewBlock != null:
                    blocks[index] = op.NewBlock;
                    break;
                case EditActionType.InsertBefore when op.NewBlock != null:
                    blocks.Insert(index, op.NewBlock);
                    break;
                case EditActionType.InsertAfter when op.NewBlock != null:
                    blocks.Insert(Math.Min(index + 1, blocks.Count), op.NewBlock);
                    break;
                case EditActionType.Remove:
                    blocks.RemoveAt(index);
                    break;
            }
        }

        RecalculateDocument(document);
    }

    public void ApplyLinearOperations(Document document, IEnumerable<LinearEditOperation> operations)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(operations);

        var linearItems = document.LinearDocument.Items.ToList();
        var blocks = document.Blocks;

        foreach (var operation in operations)
        {
            var targetIndex = ResolveLinearIndex(linearItems, operation);
            if (targetIndex < 0)
            {
                continue;
            }

            var targetPointer = operation.TargetPointer ?? linearItems[targetIndex].Pointer;
            var blockIndex = FindBlockIndex(document, targetPointer, targetIndex);
            if (blockIndex < 0)
            {
                continue;
            }

            switch (operation.Action)
            {
                case LinearEditAction.Replace:
                case LinearEditAction.Split:
                    if (operation.Items.Count == 0)
                    {
                        continue;
                    }

                    ReplaceRange(linearItems, targetIndex, 1, operation.Items);
                    ReplaceRange(blocks, blockIndex, 1, operation.Items.Select(ToBlock));
                    break;
                case LinearEditAction.InsertBefore:
                    if (operation.Items.Count == 0)
                    {
                        continue;
                    }

                    ReplaceRange(linearItems, targetIndex, 0, operation.Items);
                    ReplaceRange(blocks, blockIndex, 0, operation.Items.Select(ToBlock));
                    break;
                case LinearEditAction.InsertAfter:
                    if (operation.Items.Count == 0)
                    {
                        continue;
                    }

                    ReplaceRange(linearItems, targetIndex + 1, 0, operation.Items);
                    ReplaceRange(blocks, blockIndex + 1, 0, operation.Items.Select(ToBlock));
                    break;
                case LinearEditAction.Remove:
                    linearItems.RemoveAt(targetIndex);
                    if (blockIndex < blocks.Count)
                    {
                        blocks.RemoveAt(blockIndex);
                    }
                    break;
                case LinearEditAction.MergeWithNext:
                    MergeWithNeighbor(linearItems, blocks, targetIndex, blockIndex, 1, operation.Items);
                    break;
                case LinearEditAction.MergeWithPrevious:
                    MergeWithNeighbor(linearItems, blocks, targetIndex, blockIndex, -1, operation.Items);
                    break;
            }
        }

        document.LinearDocument.Items = linearItems;
        RecalculateDocument(document);
    }

    private void MergeWithNeighbor(
        List<LinearItem> linearItems,
        List<Block> blocks,
        int targetIndex,
        int blockIndex,
        int neighborOffset,
        IReadOnlyCollection<LinearItem> replacements)
    {
        var neighborIndex = targetIndex + neighborOffset;
        if (neighborIndex < 0 || neighborIndex >= linearItems.Count)
        {
            return;
        }

        var mergedItem = replacements.FirstOrDefault() ?? MergeItems(linearItems[targetIndex], linearItems[neighborIndex]);
        var blockNeighborIndex = blockIndex + neighborOffset;
        if (blockNeighborIndex < 0 || blockNeighborIndex >= blocks.Count)
        {
            return;
        }

        var firstIndex = Math.Min(targetIndex, neighborIndex);
        ReplaceRange(linearItems, firstIndex, 2, new[] { mergedItem });

        var firstBlockIndex = Math.Min(blockIndex, blockNeighborIndex);
        ReplaceRange(blocks, firstBlockIndex, 2, new[] { ToBlock(mergedItem) });
    }

    private static LinearItem MergeItems(LinearItem first, LinearItem second)
    {
        return new LinearItem
        {
            Markdown = string.Join("\n\n", new[] { first.Markdown, second.Markdown }.Where(s => !string.IsNullOrWhiteSpace(s))),
            Text = string.Join("\n\n", new[] { first.Text, second.Text }.Where(s => !string.IsNullOrWhiteSpace(s))),
            Type = first.Type,
            Level = first.Level,
            Pointer = first.Pointer
        };
    }

    private static int ResolveLinearIndex(List<LinearItem> items, LinearEditOperation operation)
    {
        if (operation.TargetPointer != null)
        {
            var semantic = operation.TargetPointer.SemanticNumber;
            return items.FindIndex(i => string.Equals(i.Pointer.SemanticNumber, semantic, StringComparison.OrdinalIgnoreCase));
        }

        if (operation.TargetIndex.HasValue && operation.TargetIndex.Value >= 0 && operation.TargetIndex.Value < items.Count)
        {
            return operation.TargetIndex.Value;
        }

        return -1;
    }

    private int FindBlockIndex(Document document, LinearPointer pointer, int fallbackLinearIndex)
    {
        var semanticNumber = pointer.SemanticNumber;
        var match = document.Blocks.FindIndex(b => string.Equals(b.StructuralPath, semanticNumber, StringComparison.OrdinalIgnoreCase));
        if (match >= 0)
        {
            return match;
        }

        if (fallbackLinearIndex >= 0 && fallbackLinearIndex < document.Blocks.Count)
        {
            return fallbackLinearIndex;
        }

        return -1;
    }

    private static void ReplaceRange<T>(List<T> list, int startIndex, int count, IEnumerable<T> replacements)
    {
        if (startIndex < 0 || startIndex > list.Count)
        {
            return;
        }

        if (count > 0 && startIndex < list.Count)
        {
            list.RemoveRange(startIndex, Math.Min(count, list.Count - startIndex));
        }

        list.InsertRange(startIndex, replacements);
    }

    private static Block ToBlock(LinearItem item)
    {
        return new Block
        {
            Id = Guid.NewGuid().ToString(),
            Type = MapLinearType(item.Type),
            Level = item.Level ?? 0,
            Markdown = item.Markdown,
            PlainText = item.Text
        };
    }

    private static BlockType MapLinearType(LinearItemType type)
    {
        return type switch
        {
            LinearItemType.Heading => BlockType.Heading,
            LinearItemType.ListItem => BlockType.ListItem,
            LinearItemType.Code => BlockType.Code,
            LinearItemType.ThematicBreak => BlockType.ThematicBreak,
            _ => BlockType.Paragraph
        };
    }

    private void RecalculateDocument(Document document)
    {
        var headingNumbers = new List<int>();
        var headingTitles = new List<string>();
        var linearItems = new List<LinearItem>();
        var paragraphCounter = 0;
        var index = 0;

        foreach (var block in document.Blocks)
        {
            if (block.Type == BlockType.Heading)
            {
                paragraphCounter = 0;
                UpdateHeadingNumbers(headingNumbers, block.Level);
                EnsureHeadingTitles(headingTitles, block.Level, block.PlainText);

                block.Numbering = FormatHeadingNumber(headingNumbers);
                block.StructuralPath = block.Numbering;
                block.HeadingPath = string.Join(" > ", headingTitles.Where(t => !string.IsNullOrWhiteSpace(t)));

                var pointer = new SemanticPointer(GetHeadingSlice(headingNumbers), null);
                linearItems.Add(ToLinearItem(block, index, pointer));
                index++;
                continue;
            }

            paragraphCounter++;
            var semanticPointer = new SemanticPointer(GetHeadingSlice(headingNumbers), paragraphCounter);
            block.StructuralPath = semanticPointer.SemanticNumber;
            block.HeadingPath = string.Join(" > ", headingTitles.Where(t => !string.IsNullOrWhiteSpace(t)));
            block.Numbering = headingNumbers.All(n => n == 0) ? null : FormatHeadingNumber(headingNumbers);

            linearItems.Add(ToLinearItem(block, index, semanticPointer));
            index++;
        }

        document.LinearDocument = new LinearDocument
        {
            Items = linearItems,
            SourceText = repository.SaveToMarkdown(document)
        };

        document.SourceText = document.LinearDocument.SourceText;
    }

    private static void UpdateHeadingNumbers(List<int> headingNumbers, int level)
    {
        var normalizedLevel = Math.Max(level, 1);
        while (headingNumbers.Count < normalizedLevel)
        {
            headingNumbers.Add(0);
        }

        if (headingNumbers.Count > normalizedLevel)
        {
            headingNumbers.RemoveRange(normalizedLevel, headingNumbers.Count - normalizedLevel);
        }

        headingNumbers[normalizedLevel - 1]++;
        for (var i = normalizedLevel; i < headingNumbers.Count; i++)
        {
            headingNumbers[i] = 0;
        }
    }

    private static void EnsureHeadingTitles(List<string> headingTitles, int level, string title)
    {
        var normalizedLevel = Math.Max(level, 1);
        while (headingTitles.Count < normalizedLevel)
        {
            headingTitles.Add(string.Empty);
        }

        if (headingTitles.Count > normalizedLevel)
        {
            headingTitles.RemoveRange(normalizedLevel, headingTitles.Count - normalizedLevel);
        }

        headingTitles[normalizedLevel - 1] = title;
    }

    private static IEnumerable<int> GetHeadingSlice(List<int> headingNumbers)
    {
        var lastNonZero = headingNumbers.FindLastIndex(n => n > 0);
        if (lastNonZero < 0)
        {
            return Array.Empty<int>();
        }

        return headingNumbers.Take(lastNonZero + 1).ToArray();
    }

    private static string FormatHeadingNumber(List<int> headingNumbers)
    {
        var slice = GetHeadingSlice(headingNumbers);
        return string.Join('.', slice);
    }

    private static LinearItem ToLinearItem(Block block, int index, SemanticPointer pointer)
    {
        return new LinearItem
        {
            Index = index,
            Type = MapLinearType(block.Type),
            Level = block.Type == BlockType.Heading ? block.Level : null,
            Markdown = block.Markdown,
            Text = block.PlainText,
            Pointer = new LinearPointer(index, pointer)
        };
    }

    private static LinearItemType MapLinearType(BlockType type)
    {
        return type switch
        {
            BlockType.Heading => LinearItemType.Heading,
            BlockType.ListItem => LinearItemType.ListItem,
            BlockType.Code => LinearItemType.Code,
            BlockType.ThematicBreak => LinearItemType.ThematicBreak,
            _ => LinearItemType.Paragraph
        };
    }
}
