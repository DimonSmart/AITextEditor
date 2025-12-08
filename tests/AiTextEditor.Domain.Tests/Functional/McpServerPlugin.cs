using System.Linq;
using AiTextEditor.Lib.Services;
using Microsoft.SemanticKernel;

namespace AiTextEditor.Domain.Tests.Functional;

public sealed class McpServerPlugin
{
    private readonly McpServer server;

    public McpServerPlugin(McpServer server)
    {
        this.server = server ?? throw new ArgumentNullException(nameof(server));
    }

    [KernelFunction("list_pointers")]
    public string ListPointers()
    {
        var items = server.GetItems();
        return string.Join("\n", items.Select(item => $"{item.Pointer.SemanticNumber}: {item.Text}"));
    }

    [KernelFunction("get_default_document_id")]
    public string GetDefaultDocumentId()
    {
        return server.ListDefaultTargetSets().FirstOrDefault()?.DocumentId ?? server.GetDefaultDocument().Id;
    }
}
