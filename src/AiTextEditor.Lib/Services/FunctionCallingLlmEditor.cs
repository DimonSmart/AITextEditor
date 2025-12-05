using System.Text;
using System.Text.Json;
using AiTextEditor.Lib.Interfaces;
using AiTextEditor.Lib.Model;

namespace AiTextEditor.Lib.Services;

/// <summary>
/// Uses an LLM to translate a user request into <see cref="EditOperation"/>s.
/// The model is asked to emit a compact JSON array of operations.
/// </summary>
public class FunctionCallingLlmEditor : ILlmEditor
{
    private readonly ILlmClient llmClient;

    public FunctionCallingLlmEditor(ILlmClient llmClient)
    {
        this.llmClient = llmClient;
    }

    public async Task<List<EditOperation>> GetEditOperationsAsync(
        List<Block> contextBlocks,
        string rawUserText,
        string instruction,
        CancellationToken ct = default)
    {
        var prompt = BuildPrompt(contextBlocks, rawUserText, instruction);
        var response = await llmClient.CompleteAsync(prompt, ct);
        return ParseOperations(response);
    }

    private static string BuildPrompt(List<Block> contextBlocks, string userRequest, string instruction)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Ты — редактор markdown документов. Пользователь описывает правки, а ты возвращаешь JSON-массив команд редактирования.");
        sb.AppendLine("Используй только следующие действия:");
        sb.AppendLine(" - replace: заменить содержимое блока.");
        sb.AppendLine(" - insert_after: вставить новый блок сразу после указанного.");
        sb.AppendLine(" - insert_before: вставить новый блок сразу перед указанным.");
        sb.AppendLine(" - remove: удалить указанный блок.");
        sb.AppendLine("Форма ответа:");
        sb.AppendLine(@"[
  {
    ""action"": ""replace | insert_after | insert_before | remove"",
    ""targetBlockId"": ""existing block id"",
    ""blockType"": ""paragraph | heading | code | quote | listitem"",
    ""level"": 2, // только для заголовков
    ""markdown"": ""новый markdown"",
    ""plainText"": ""тот же текст без разметки"",
    ""parentId"": ""если нужно сохранить иерархию""
  }
]");
        sb.AppendLine("Никакого текста вне JSON.");
        sb.AppendLine();
        sb.AppendLine("Контекст (blockId | type | level | markdown):");

        foreach (var block in contextBlocks)
        {
            var compact = block.Markdown.Replace("\n", "\\n").Replace("\r", string.Empty);
            sb.AppendLine($"- {block.Id} | {block.Type} | L{block.Level} | {compact}");
        }

        sb.AppendLine();
        sb.AppendLine($"Запрос пользователя: {userRequest}");
        sb.AppendLine("Цель правки/ограничения: ");
        sb.AppendLine(instruction);
        sb.AppendLine("Ответь строго JSON массивом без пояснений.");

        return sb.ToString();
    }

    private static List<EditOperation> ParseOperations(string rawResponse)
    {
        var clean = StripJsonFence(rawResponse);
        var result = new List<EditOperation>();

        if (string.IsNullOrWhiteSpace(clean))
        {
            return result;
        }

        try
        {
            using var doc = JsonDocument.Parse(clean);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Object &&
                TryGetPropertyIgnoreCase(root, "operations", out var opsProp))
            {
                root = opsProp;
            }

            if (root.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (var element in root.EnumerateArray())
            {
                var operation = ParseOperation(element);
                if (operation != null)
                {
                    result.Add(operation);
                }
            }
        }
        catch (JsonException)
        {
            // Ignore malformed model output
        }

        return result;
    }

    private static EditOperation? ParseOperation(JsonElement element)
    {
        if (!TryGetString(element, "action", out var actionString))
        {
            return null;
        }

        var action = ParseAction(actionString);
        var targetBlockId = TryGetString(element, "targetBlockId", out var target) ? target : null;

        Block? newBlock = null;
        if (action is EditActionType.InsertAfter or EditActionType.InsertBefore or EditActionType.Replace)
        {
            newBlock = ParseBlock(element);
            if (newBlock == null)
            {
                return null;
            }
        }

        return new EditOperation
        {
            Action = action,
            TargetBlockId = targetBlockId,
            NewBlock = newBlock
        };
    }

    private static Block? ParseBlock(JsonElement element)
    {
        if (!TryGetString(element, "markdown", out var markdown))
        {
            markdown = string.Empty;
        }

        TryGetString(element, "plainText", out var plainText);
        TryGetString(element, "blockType", out var blockTypeRaw);
        TryGetString(element, "parentId", out var parentId);
        TryGetString(element, "newBlockId", out var newBlockId);

        int level = 0;
        if (TryGetPropertyIgnoreCase(element, "level", out var levelProp) && levelProp.ValueKind == JsonValueKind.Number)
        {
            level = levelProp.GetInt32();
        }

        var block = new Block
        {
            Id = string.IsNullOrWhiteSpace(newBlockId) ? Guid.NewGuid().ToString() : newBlockId,
            Markdown = markdown,
            PlainText = string.IsNullOrWhiteSpace(plainText) ? markdown : plainText,
            ParentId = parentId,
            Level = level
        };

        block.Type = ParseBlockType(blockTypeRaw);
        return block;
    }

    private static EditActionType ParseAction(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "replace" => EditActionType.Replace,
            "insert_after" => EditActionType.InsertAfter,
            "insertbefore" => EditActionType.InsertBefore,
            "insert_before" => EditActionType.InsertBefore,
            "remove" => EditActionType.Remove,
            _ => EditActionType.Keep
        };
    }

    private static BlockType ParseBlockType(string? value)
    {
        return value?.ToLowerInvariant() switch
        {
            "heading" => BlockType.Heading,
            "paragraph" => BlockType.Paragraph,
            "listitem" => BlockType.ListItem,
            "code" => BlockType.Code,
            "quote" => BlockType.Quote,
            "thematicbreak" => BlockType.ThematicBreak,
            "html" => BlockType.Html,
            _ => BlockType.Paragraph
        };
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string value)
    {
        if (TryGetPropertyIgnoreCase(element, propertyName, out var prop) &&
            prop.ValueKind is JsonValueKind.String or JsonValueKind.Number)
        {
            value = prop.ToString();
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement property)
    {
        foreach (var prop in element.EnumerateObject())
        {
            if (string.Equals(prop.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                property = prop.Value;
                return true;
            }
        }

        property = default;
        return false;
    }

    private static string StripJsonFence(string raw)
    {
        var trimmed = raw.Trim();

        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var idx = trimmed.IndexOf('\n');
            if (idx >= 0)
            {
                trimmed = trimmed[(idx + 1)..];
            }
        }

        if (trimmed.EndsWith("```", StringComparison.Ordinal))
        {
            trimmed = trimmed[..^3];
        }

        return trimmed.Trim();
    }
}
