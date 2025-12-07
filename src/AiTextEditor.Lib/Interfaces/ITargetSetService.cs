using AiTextEditor.Lib.Model;

namespace AiTextEditor.Lib.Interfaces;

public interface ITargetSetService
{
    TargetSet Create(
        string documentId,
        IEnumerable<LinearItem> items,
        string? intentJson = null,
        string? label = null,
        Func<LinearItem, string?>? blockIdResolver = null);

    TargetSet? Get(string targetSetId);

    IReadOnlyList<TargetSet> List(string? documentId = null);

    bool Delete(string targetSetId);
}
