using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AiTextEditor.Lib.Interfaces;
using AiTextEditor.Lib.Model;

namespace AiTextEditor.Lib.Services;

public class BulkOperationService
{
    private readonly IDocumentEditor documentEditor;

    public BulkOperationService(IDocumentEditor? documentEditor = null)
    {
        this.documentEditor = documentEditor ?? new DocumentEditor();
    }

    public async IAsyncEnumerable<BulkOperationProgress> ReplaceTextInTargetsAsync(
        Document document,
        TargetSet targetSet,
        Func<LinearItem, string> replacementFactory,
        int batchSize = 10,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var progress in ApplyAsync(
                           document,
                           targetSet,
                           item => Task.FromResult(CreateReplaceOperation(item, replacementFactory(item))),
                           batchSize,
                           ct))
        {
            yield return progress;
        }
    }

    public async IAsyncEnumerable<BulkOperationProgress> RewriteTargetsWithInstructionsAsync(
        Document document,
        TargetSet targetSet,
        Func<LinearItem, string, Task<LinearItem>> rewriteFactory,
        string instruction,
        int batchSize = 10,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var progress in ApplyAsync(
                           document,
                           targetSet,
                           async item => new LinearEditOperation
                           {
                               Action = LinearEditAction.Replace,
                               TargetPointer = item.Pointer,
                               Items = { await rewriteFactory(item, instruction) }
                           },
                           batchSize,
                           ct))
        {
            yield return progress;
        }
    }

    public async IAsyncEnumerable<BulkOperationProgress> ApplyAsync(
        Document document,
        TargetSet targetSet,
        Func<LinearItem, Task<LinearEditOperation?>> operationFactory,
        int batchSize = 10,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(targetSet);
        ArgumentNullException.ThrowIfNull(operationFactory);

        var pending = new List<LinearEditOperation>();
        var processed = 0;
        var total = targetSet.Targets.Count;

        foreach (var target in targetSet.Targets.OrderBy(t => t.LinearIndex))
        {
            ct.ThrowIfCancellationRequested();

            var linearItem = ResolveTargetItem(document, target);
            if (linearItem != null)
            {
                var operation = await operationFactory(linearItem);
                if (operation != null)
                {
                    if (operation.TargetPointer == null && operation.TargetIndex == null)
                    {
                        operation.TargetPointer = linearItem.Pointer;
                    }

                    pending.Add(operation);
                }
            }

            processed++;

            if (pending.Count >= batchSize)
            {
                documentEditor.ApplyLinearOperations(document, pending);
                pending.Clear();
                yield return new BulkOperationProgress(processed, total);
            }
        }

        if (pending.Count > 0)
        {
            documentEditor.ApplyLinearOperations(document, pending);
            yield return new BulkOperationProgress(processed, total);
        }

        if (total == 0)
        {
            yield return new BulkOperationProgress(0, 0);
        }
    }

    private static LinearItem? ResolveTargetItem(Document document, TargetRef target)
    {
        var byIndex = document.LinearDocument.Items
            .FirstOrDefault(i => i.Index == target.LinearIndex);

        if (byIndex != null)
        {
            return byIndex;
        }

        var semantic = target.Pointer.SemanticNumber;
        return document.LinearDocument.Items
            .FirstOrDefault(i => string.Equals(i.Pointer.SemanticNumber, semantic, StringComparison.OrdinalIgnoreCase));
    }

    private static LinearEditOperation CreateReplaceOperation(LinearItem target, string newContent)
    {
        var replacement = new LinearItem
        {
            Markdown = newContent,
            Text = newContent,
            Type = target.Type,
            Level = target.Level,
            Pointer = target.Pointer
        };

        return new LinearEditOperation
        {
            Action = LinearEditAction.Replace,
            TargetPointer = target.Pointer,
            Items = { replacement }
        };
    }
}
