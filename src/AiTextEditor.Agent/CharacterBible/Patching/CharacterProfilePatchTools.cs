using System.ComponentModel;
using System.Text.RegularExpressions;
using AiTextEditor.Agent.CharacterBible.Diagnostics;
using AiTextEditor.Core.Model;

namespace AiTextEditor.Agent.CharacterBible.Patching;

public enum SetProfileFieldResultStatus
{
    Applied,
    NoOp,
    Rejected,
    Conflict
}

public sealed record SetProfileFieldResult
{
    public required SetProfileFieldResultStatus Status { get; init; }

    public string? Message { get; init; }

    public string? CurrentValue { get; init; }
}

internal sealed class CharacterProfilePatchContext
{
    public CharacterProfilePatchContext(
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

internal sealed class CharacterProfilePatchStatistics
{
    public int CharactersProcessed { get; set; }

    public int AgentCalls { get; set; }

    public int ToolCalls { get; set; }

    public int Applied { get; set; }

    public int NoOp { get; set; }

    public int Rejected { get; set; }

    public int Conflict { get; set; }

    public int ProfileFieldsChanged => Applied;
}

public sealed class CharacterProfilePatchTools
{
    private const int MaxProfileFieldLength = 500;
    private const int LongParagraphLength = 240;

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex Markdown = new(@"(^|\s)(#{1,6}\s|[-*+]\s|>\s|```)|(\[[^\]]+\]\([^)]+\))|(\*\*|__|`)", RegexOptions.Compiled);

    private readonly string characterId;
    private readonly string characterName;
    private readonly CharacterProfilePatchContext context;
    private readonly CharacterDossierEditSession store;
    private readonly CharacterProfilePatchStatistics statistics;

    internal CharacterProfilePatchTools(
        string characterId,
        string characterName,
        CharacterProfilePatchContext context,
        CharacterDossierEditSession store,
        CharacterProfilePatchStatistics statistics)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(characterId);
        ArgumentException.ThrowIfNullOrWhiteSpace(characterName);
        this.characterId = characterId;
        this.characterName = characterName;
        this.context = context ?? throw new ArgumentNullException(nameof(context));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.statistics = statistics ?? throw new ArgumentNullException(nameof(statistics));
    }

    [Description("Sets one evidence-backed profile field for the current character.")]
    public SetProfileFieldResult SetProfileField(
        [Description("Profile field to set: Appearance, StatusAndCompetence, PsychologicalProfile, or SpeechAndCommunication.")] CharacterBibleProfileField field,
        [Description("Concise factual value supported by the supplied evidence pointers. Do not use Markdown.")] string value,
        [Description("One or more pointers from the evidence supplied for the current character.")] IReadOnlyList<string> evidencePointers)
    {
        statistics.ToolCalls++;

        var result = ValidateAndApply(field, value, evidencePointers);
        Increment(result.Status);
        CharacterBibleRunLogScope.Current?.Info(
            "patch.tool.set_profile_field",
            $"character={LogValueFormatter.Quote(characterName)} field={field} status={result.Status} evidencePointers={LogValueFormatter.List(evidencePointers ?? [])} valueLength={value?.Length ?? 0}");
        CharacterBibleRunLogScope.Current?.Debug(
            "patch.tool.set_profile_field.value",
            $"character={LogValueFormatter.Quote(characterName)} field={field} value={LogValueFormatter.Quote(value)}");
        return result;
    }

    private SetProfileFieldResult ValidateAndApply(
        CharacterBibleProfileField field,
        string value,
        IReadOnlyList<string> evidencePointers)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Rejected("Value is empty.");
        }

        if (!Enum.IsDefined(field))
        {
            return Rejected("Profile field is unsupported.");
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
            return new SetProfileFieldResult
            {
                Status = SetProfileFieldResultStatus.NoOp,
                CurrentValue = currentValue
            };
        }

        if (!string.IsNullOrWhiteSpace(currentValue))
        {
            return new SetProfileFieldResult
            {
                Status = SetProfileFieldResultStatus.Conflict,
                Message = "Field already contains a different non-empty value.",
                CurrentValue = currentValue
            };
        }

        context.CurrentProfile = SetField(context.CurrentProfile, field, normalizedValue);
        store.UpdateProfile(characterId, context.CurrentProfile);
        return new SetProfileFieldResult
        {
            Status = SetProfileFieldResultStatus.Applied,
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

    private static SetProfileFieldResult Rejected(string message)
        => new()
        {
            Status = SetProfileFieldResultStatus.Rejected,
            Message = message
        };

    private void Increment(SetProfileFieldResultStatus status)
    {
        switch (status)
        {
            case SetProfileFieldResultStatus.Applied:
                statistics.Applied++;
                break;
            case SetProfileFieldResultStatus.NoOp:
                statistics.NoOp++;
                break;
            case SetProfileFieldResultStatus.Rejected:
                statistics.Rejected++;
                break;
            case SetProfileFieldResultStatus.Conflict:
                statistics.Conflict++;
                break;
        }
    }
}
