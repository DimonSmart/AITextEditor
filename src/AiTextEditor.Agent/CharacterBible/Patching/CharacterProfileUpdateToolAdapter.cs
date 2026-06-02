using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
using AiTextEditor.Agent.CharacterBible.Diagnostics;
using AiTextEditor.Core.Model;

namespace AiTextEditor.Agent.CharacterBible.Patching;

[JsonConverter(typeof(JsonStringEnumConverter<CharacterBibleProfileField>))]
public enum CharacterBibleProfileField
{
    Appearance,
    StatusAndCompetence,
    PsychologicalProfile,
    SpeechAndCommunication
}

public enum ReplaceProfileFieldResultStatus
{
    Applied,
    NoOp,
    Rejected
}

public sealed record ReplaceProfileFieldResult
{
    public required ReplaceProfileFieldResultStatus Status { get; init; }

    public string? Message { get; init; }

    public string? CurrentValue { get; init; }
}

internal sealed class CharacterProfileUpdateContext
{
    public CharacterProfileUpdateContext(
        CharacterProfile? currentProfile,
        IReadOnlySet<string> allowedEvidencePointers,
        IReadOnlyDictionary<string, string> evidenceTextByPointer)
    {
        CurrentProfile = CharacterProfile.Normalize(currentProfile);
        AllowedEvidencePointers = allowedEvidencePointers ?? throw new ArgumentNullException(nameof(allowedEvidencePointers));
        EvidenceTextByPointer = evidenceTextByPointer ?? throw new ArgumentNullException(nameof(evidenceTextByPointer));
    }

    public CharacterProfile CurrentProfile { get; set; }

    public IReadOnlySet<string> AllowedEvidencePointers { get; }

    public IReadOnlyDictionary<string, string> EvidenceTextByPointer { get; }
}

internal sealed class CharacterProfileUpdateStatistics
{
    public int CharactersProcessed { get; set; }

    public int AgentCalls { get; set; }

    public int ToolCalls { get; set; }

    public int Applied { get; set; }

    public int NoOp { get; set; }

    public int Rejected { get; set; }

    public int ProfileFieldsChanged => Applied;
}

public sealed class CharacterProfileUpdateToolAdapter
{
    private const int MaxProfileFieldLength = 500;
    private const int LongParagraphLength = 240;

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex Markdown = new(@"(^|\s)(#{1,6}\s|[-*+]\s|>\s|```)|(\[[^\]]+\]\([^)]+\))|(\*\*|__|`)", RegexOptions.Compiled);

    private readonly int characterId;
    private readonly string characterName;
    private readonly CharacterProfileUpdateContext context;
    private readonly CharacterDossierEditSession store;
    private readonly CharacterProfileUpdateStatistics statistics;

    internal CharacterProfileUpdateToolAdapter(
        int characterId,
        string characterName,
        CharacterProfileUpdateContext context,
        CharacterDossierEditSession store,
        CharacterProfileUpdateStatistics statistics)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(characterName);
        this.characterId = characterId;
        this.characterName = characterName;
        this.context = context ?? throw new ArgumentNullException(nameof(context));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.statistics = statistics ?? throw new ArgumentNullException(nameof(statistics));
        this.store.GetRequired(characterId);
    }

    [Description("Replaces one evidence-backed profile field for the current character.")]
    public ReplaceProfileFieldResult ReplaceProfileField(
        [Description("Profile field to replace: Appearance, StatusAndCompetence, PsychologicalProfile, or SpeechAndCommunication.")] CharacterBibleProfileField field,
        [Description("Complete new value of the profile field. This is a replacement, not text to append. Do not use Markdown.")] string value,
        [Description("One or more pointers from the evidence supplied for the current character.")] IReadOnlyList<string> evidencePointers,
        [Description("Short diagnostic explanation of why the evidence changes this profile field.")] string reason)
    {
        statistics.ToolCalls++;

        var result = ValidateAndApply(field, value, evidencePointers, reason);
        Increment(result.Status);
        CharacterBibleRunLogScope.Current?.Info(
            "profile.update.tool.call",
            $"characterId={characterId} name={LogValueFormatter.Quote(characterName)} field={field} status={result.Status} evidencePointers={LogValueFormatter.List(evidencePointers ?? [])} valueLength={value?.Length ?? 0} reason={LogValueFormatter.Quote(reason)}");
        CharacterBibleRunLogScope.Current?.Debug(
            "profile.update.tool.value",
            $"characterId={characterId} name={LogValueFormatter.Quote(characterName)} field={field} value={LogValueFormatter.Quote(value)}");
        return result;
    }

    private ReplaceProfileFieldResult ValidateAndApply(
        CharacterBibleProfileField field,
        string value,
        IReadOnlyList<string> evidencePointers,
        string reason)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Rejected("Value is empty.");
        }

        if (!Enum.IsDefined(field))
        {
            return Rejected("Profile field is unsupported.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return Rejected("Reason is empty.");
        }

        var normalizedValue = NormalizeWhitespace(value);
        if (normalizedValue.Length > MaxProfileFieldLength)
        {
            return Rejected("Value is too long.");
        }

        if (Markdown.IsMatch(normalizedValue))
        {
            return Rejected("Markdown is not allowed.");
        }

        if (evidencePointers is null || evidencePointers.Count == 0)
        {
            return Rejected("Evidence pointers are required.");
        }

        var normalizedPointers = evidencePointers
            .Where(pointer => !string.IsNullOrWhiteSpace(pointer))
            .Select(pointer => pointer.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (normalizedPointers.Length != evidencePointers.Count
            || normalizedPointers.Any(pointer => !context.AllowedEvidencePointers.Contains(pointer)))
        {
            return Rejected("Evidence pointer is not available in the current patch context.");
        }

        if (normalizedValue.Length >= LongParagraphLength
            && normalizedPointers.Any(pointer =>
                context.EvidenceTextByPointer.TryGetValue(pointer, out var text)
                && string.Equals(NormalizeWhitespace(text), normalizedValue, StringComparison.Ordinal)))
        {
            return Rejected("Value must not copy a long evidence paragraph.");
        }

        var currentValue = GetField(context.CurrentProfile, field);
        if (string.Equals(NormalizeWhitespace(currentValue), normalizedValue, StringComparison.Ordinal))
        {
            return new ReplaceProfileFieldResult
            {
                Status = ReplaceProfileFieldResultStatus.NoOp,
                CurrentValue = currentValue
            };
        }

        context.CurrentProfile = SetField(context.CurrentProfile, field, normalizedValue);
        store.UpdateProfile(characterId, context.CurrentProfile);
        return new ReplaceProfileFieldResult
        {
            Status = ReplaceProfileFieldResultStatus.Applied,
            CurrentValue = normalizedValue
        };
    }

    private static string? GetField(CharacterProfile profile, CharacterBibleProfileField field)
        => field switch
        {
            CharacterBibleProfileField.Appearance => profile.Appearance,
            CharacterBibleProfileField.StatusAndCompetence => profile.StatusAndCompetence,
            CharacterBibleProfileField.PsychologicalProfile => profile.PsychologicalProfile,
            CharacterBibleProfileField.SpeechAndCommunication => profile.SpeechAndCommunication,
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Unsupported profile field.")
        };

    private static CharacterProfile SetField(CharacterProfile profile, CharacterBibleProfileField field, string value)
        => field switch
        {
            CharacterBibleProfileField.Appearance => profile with { Appearance = value },
            CharacterBibleProfileField.StatusAndCompetence => profile with { StatusAndCompetence = value },
            CharacterBibleProfileField.PsychologicalProfile => profile with { PsychologicalProfile = value },
            CharacterBibleProfileField.SpeechAndCommunication => profile with { SpeechAndCommunication = value },
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Unsupported profile field.")
        };

    private static string NormalizeWhitespace(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : Whitespace.Replace(value.Trim(), " ");

    private static ReplaceProfileFieldResult Rejected(string message)
        => new()
        {
            Status = ReplaceProfileFieldResultStatus.Rejected,
            Message = message
        };

    private void Increment(ReplaceProfileFieldResultStatus status)
    {
        switch (status)
        {
            case ReplaceProfileFieldResultStatus.Applied:
                statistics.Applied++;
                break;
            case ReplaceProfileFieldResultStatus.NoOp:
                statistics.NoOp++;
                break;
            case ReplaceProfileFieldResultStatus.Rejected:
                statistics.Rejected++;
                break;
        }
    }
}
