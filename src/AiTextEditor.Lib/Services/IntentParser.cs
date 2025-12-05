using System.Text;
using System.Text.Json;
using AiTextEditor.Lib.Interfaces;
using AiTextEditor.Lib.Model.Intent;

namespace AiTextEditor.Lib.Services;

/// <summary>
/// Uses an LLM to translate a raw user command into a structured Intent DTO.
/// </summary>
public class IntentParser
{
    private readonly ILlmClient llmClient;
    private readonly JsonSerializerOptions jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public IntentParser(ILlmClient llmClient)
    {
        this.llmClient = llmClient ?? throw new ArgumentNullException(nameof(llmClient));
    }

    public async Task<IntentParseResult> ParseAsync(string userCommand, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userCommand))
        {
            return new IntentParseResult
            {
                Success = false,
                Error = "Empty user command.",
                RawResponse = string.Empty
            };
        }

        var prompt = BuildPrompt(userCommand);
        var response = await llmClient.CompleteAsync(prompt, ct);

        var result = new IntentParseResult
        {
            RawResponse = response
        };

        try
        {
            var clean = StripJsonFence(response);
            using var doc = JsonDocument.Parse(clean, new JsonDocumentOptions
            {
                AllowTrailingCommas = true
            });

            var root = doc.RootElement;
            if (TryGetPropertyIgnoreCase(root, "intent", out var intentProp))
            {
                root = intentProp;
            }

            var intent = ParseIntent(root);
            if (intent == null)
            {
                result.Success = false;
                result.Error = "Intent missing required fields.";
                return result;
            }

            result.Intent = intent;
            result.Success = true;
            return result;
        }
        catch (JsonException ex)
        {
            result.Success = false;
            result.Error = $"Failed to parse JSON: {ex.Message}";
            return result;
        }
    }

    private static IntentDto? ParseIntent(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!TryGetString(element, "scopeType", out var scopeRaw))
        {
            return null;
        }

        var scope = ParseScopeDescriptor(element);
        var payload = ParsePayload(element);

        var intent = new IntentDto
        {
            ScopeType = ParseScopeType(scopeRaw),
            ScopeDescriptor = scope,
            Payload = payload,
            RawJson = element.GetRawText()
        };

        return intent;
    }

    private static ScopeDescriptor ParseScopeDescriptor(JsonElement element)
    {
        ScopeDescriptor descriptor = new();

        if (TryGetPropertyIgnoreCase(element, "scopeDescriptor", out var scopeEl) &&
            scopeEl.ValueKind == JsonValueKind.Object)
        {
            if (TryGetInt(scopeEl, "chapterNumber", out var chapter)) descriptor.ChapterNumber = chapter;
            if (TryGetInt(scopeEl, "sectionNumber", out var section)) descriptor.SectionNumber = section;
            if (TryGetInt(scopeEl, "figureNumber", out var figure)) descriptor.FigureNumber = figure;
            if (TryGetString(scopeEl, "structuralPath", out var structuralPath)) descriptor.StructuralPath = structuralPath;
            if (TryGetString(scopeEl, "semanticQuery", out var semanticQuery)) descriptor.SemanticQuery = semanticQuery;
            if (TryGetString(scopeEl, "extraHints", out var extraHints)) descriptor.ExtraHints = extraHints;
            if (TryGetBool(scopeEl, "global", out var isGlobal)) descriptor.IsGlobal = isGlobal;
            if (TryGetBool(scopeEl, "isGlobal", out var isGlobalAlt)) descriptor.IsGlobal = descriptor.IsGlobal || isGlobalAlt;
        }

        return descriptor;
    }

    private static IntentPayload ParsePayload(JsonElement element)
    {
        var payload = new IntentPayload();

        if (TryGetPropertyIgnoreCase(element, "payload", out var payloadEl) &&
            payloadEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in payloadEl.EnumerateObject())
            {
                payload.Fields[prop.Name] = prop.Value.ToString();
            }
        }

        return payload;
    }

    private static IntentScopeType ParseScopeType(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        return normalized switch
        {
            "structural" => IntentScopeType.Structural,
            "semanticlocal" or "semantic" => IntentScopeType.SemanticLocal,
            "global" => IntentScopeType.Global,
            _ => IntentScopeType.Unknown
        };
    }

    private static string BuildPrompt(string userCommand)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are an intent parser for a Markdown editor. Convert the user command into JSON with three fields: scopeType, scopeDescriptor, payload.");
        sb.AppendLine("Return ONLY JSON. Schema:");
        sb.AppendLine("{");
        sb.AppendLine("  \"scopeType\": \"Structural | SemanticLocal | Global\",");
        sb.AppendLine("  \"scopeDescriptor\": {");
        sb.AppendLine("     \"chapterNumber\": number?,");
        sb.AppendLine("     \"sectionNumber\": number?,");
        sb.AppendLine("     \"figureNumber\": number?,");
        sb.AppendLine("     \"structuralPath\": string?,");
        sb.AppendLine("     \"semanticQuery\": string?,");
        sb.AppendLine("     \"extraHints\": string?,");
        sb.AppendLine("     \"global\": boolean?");
        sb.AppendLine("  },");
        sb.AppendLine("  \"payload\": { ... }");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("Examples:");
        sb.AppendLine("User: \"Добавь TODO во вторую главу: проверить, что все примеры компилируются.\"");
        sb.AppendLine("{");
        sb.AppendLine("  \"scopeType\": \"Structural\",");
        sb.AppendLine("  \"scopeDescriptor\": { \"chapterNumber\": 2 },");
        sb.AppendLine("  \"payload\": { \"todoText\": \"Проверить, что все примеры компилируются.\" }");
        sb.AppendLine("}");
        sb.AppendLine("User: \"Во всей книге замени персонажа Петя на Вася.\"");
        sb.AppendLine("{");
        sb.AppendLine("  \"scopeType\": \"Global\",");
        sb.AppendLine("  \"scopeDescriptor\": { \"global\": true },");
        sb.AppendLine("  \"payload\": { \"from\": \"Петя\", \"to\": \"Вася\", \"entityType\": \"characterName\" }");
        sb.AppendLine("}");
        sb.AppendLine("User: \"Найди место, где я объясняю разницу между монолитом и микросервисами, и сделай объяснение проще для джуна.\"");
        sb.AppendLine("{");
        sb.AppendLine("  \"scopeType\": \"SemanticLocal\",");
        sb.AppendLine("  \"scopeDescriptor\": { \"semanticQuery\": \"difference between monolith and microservices\" },");
        sb.AppendLine("  \"payload\": { \"style\": \"simpler\", \"audience\": \"junior backend\" }");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.Append("User command to parse: ");
        sb.AppendLine(userCommand);
        sb.AppendLine("Now return only the JSON object.");
        return sb.ToString();
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string value)
    {
        if (TryGetPropertyIgnoreCase(element, propertyName, out var prop) &&
            prop.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
        {
            value = prop.ToString();
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool TryGetInt(JsonElement element, string propertyName, out int value)
    {
        if (TryGetPropertyIgnoreCase(element, propertyName, out var prop) &&
            prop.ValueKind == JsonValueKind.Number &&
            prop.TryGetInt32(out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    private static bool TryGetBool(JsonElement element, string propertyName, out bool value)
    {
        if (TryGetPropertyIgnoreCase(element, propertyName, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.True)
            {
                value = true;
                return true;
            }

            if (prop.ValueKind == JsonValueKind.False)
            {
                value = false;
                return true;
            }

            if (prop.ValueKind == JsonValueKind.String &&
                bool.TryParse(prop.GetString(), out var parsed))
            {
                value = parsed;
                return true;
            }
        }

        value = default;
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

        if (trimmed.StartsWith("```"))
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
