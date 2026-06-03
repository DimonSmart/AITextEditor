using System.Text.Json;
using AiTextEditor.Agent.CharacterBible.Patching;

namespace AiTextEditor.Agent.CharacterBible.Diagnostics;

internal static class CharacterBibleLlmInputLogger
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    };

    public static void DebugInput(string eventName, string header, object input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(input);

        CharacterBibleRunLogScope.Current?.DebugBlock(
            eventName,
            header,
            JsonSerializer.Serialize(input, JsonOptions));
    }

    public static void DebugProfileUpdateContract(CharacterProfileUpdatePromptInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(input, JsonOptions));
        var topLevelKeys = json.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
        var forbiddenKeys = new HashSet<string>(
            [
                "candidateId",
                "characterId",
                "identityDecision",
                "candidateIds",
                "observedNameForms"
            ],
            StringComparer.OrdinalIgnoreCase);
        var forbiddenKeysFound = FindKeys(json.RootElement, forbiddenKeys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var emptyEvidenceTexts = input.NewEvidence
            .Where(evidence => string.IsNullOrWhiteSpace(evidence.Text))
            .Select(evidence => evidence.Pointer)
            .ToArray();

        CharacterBibleRunLogScope.Current?.Debug(
            "profile.update.llm.input.contract",
            $"topLevelKeys={LogValueFormatter.List(topLevelKeys)} forbiddenKeysFound={LogValueFormatter.List(forbiddenKeysFound)} evidenceCount={input.NewEvidence.Count} emptyEvidenceTexts={LogValueFormatter.List(emptyEvidenceTexts)}");
    }

    private static IEnumerable<string> FindKeys(JsonElement element, IReadOnlySet<string> keys)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (keys.Contains(property.Name))
                    {
                        yield return property.Name;
                    }

                    foreach (var nested in FindKeys(property.Value, keys))
                    {
                        yield return nested;
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var nested in FindKeys(item, keys))
                    {
                        yield return nested;
                    }
                }

                break;
        }
    }
}
