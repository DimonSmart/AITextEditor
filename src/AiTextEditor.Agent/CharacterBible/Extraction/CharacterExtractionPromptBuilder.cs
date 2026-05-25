using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiTextEditor.Agent.CharacterBible.Extraction;

public sealed class CharacterExtractionPromptBuilder
{
    private const string SystemPromptResourceName = "AiTextEditor.Agent.CharacterBible.Extraction.Prompts.character-extraction.system.md";

    private static readonly Lazy<string> SystemPrompt = new(LoadSystemPrompt);

    private static readonly JsonSerializerOptions UserPromptJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    public string BuildSystemPrompt() => SystemPrompt.Value;

    public string BuildUserPrompt(IReadOnlyList<(string Pointer, string Text)> paragraphs)
    {
        ArgumentNullException.ThrowIfNull(paragraphs);

        var payload = new
        {
            task = "extract_characters",
            paragraphs = paragraphs.Select(paragraph => new { pointer = paragraph.Pointer, text = paragraph.Text })
        };

        return JsonSerializer.Serialize(payload, UserPromptJsonOptions);
    }

    private static string LoadSystemPrompt()
    {
        var assembly = typeof(CharacterExtractionPromptBuilder).Assembly;
        using var stream = assembly.GetManifestResourceStream(SystemPromptResourceName);
        if (stream is null)
        {
            throw new InvalidOperationException($"Embedded character extraction prompt resource '{SystemPromptResourceName}' was not found.");
        }

        using var reader = new StreamReader(stream);
        var prompt = reader.ReadToEnd();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new InvalidOperationException($"Embedded character extraction prompt resource '{SystemPromptResourceName}' is empty.");
        }

        return prompt.Trim();
    }
}

