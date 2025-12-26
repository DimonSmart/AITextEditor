using AiTextEditor.Lib.Common;
using AiTextEditor.Lib.Services;
using AiTextEditor.Lib.Services.SemanticKernel;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using System.ComponentModel;
using System.Text.Json;

namespace AiTextEditor.SemanticKernel;

public sealed class ChatCursorTools(
    ICursorStore registry,
    CursorAgentLimits limits,
    ILogger<ChatCursorTools> logger)
{
    [KernelFunction("read_cursor_batch")]
    [Description("Reads the next portion from an existing cursor and returns items with pointers.")]
    public string ReadCursorBatch(
        [Description("Cursor name created earlier.")] string cursorName)
    {
        if (!registry.TryGetCursor(cursorName, out var cursor) || cursor == null)
        {
            throw new InvalidOperationException($"Cursor '{cursorName}' not found.");
        }

        var portion = cursor.NextPortion();
        var portionView = CursorPortionView.FromPortion(portion);
        var response = new
        {
            items = portionView.Items,
            portionView.HasMore,
            nextAfterPointer = portionView.Items.Count > 0 ? portionView.Items[^1].SemanticPointer : null,
            limits.MaxElements,
            limits.MaxBytes
        };

        logger.LogDebug("read_cursor_batch: cursor={Cursor}, count={Count}, hasMore={HasMore}", cursorName, portionView.Items.Count, portionView.HasMore);

        return JsonSerializer.Serialize(response, SerializationOptions.RelaxedCompact);
    }
}
