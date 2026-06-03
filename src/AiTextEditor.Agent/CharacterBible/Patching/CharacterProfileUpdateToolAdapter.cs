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
    public CharacterProfileUpdateContext(CharacterProfile? currentProfile)
    {
        CurrentProfile = CharacterProfile.Normalize(currentProfile);
    }

    public CharacterProfile CurrentProfile { get; set; }
}

internal sealed class CharacterProfileUpdateStatistics
{
    public int CharactersProcessed { get; set; }

    public int AgentCalls { get; set; }

    public int ToolCalls { get; set; }

    public int Applied { get; set; }

    public int NoOp { get; set; }

    public int Rejected { get; set; }

    public List<CharacterBibleProfileField> AppliedFields { get; } = [];

    public List<CharacterProfileUpdateRejectedToolCall> RejectedToolCalls { get; } = [];

    public int ProfileFieldsChanged => Applied;
}

internal sealed record CharacterProfileUpdateRejectedToolCall(
    string ToolName,
    string Field,
    string RuleCode,
    string Message,
    string RejectedValuePreview,
    IReadOnlyList<string> EvidencePointers);

public sealed class CharacterProfileUpdateToolAdapter
{
    private const int MaxProfileFieldLength = 500;
    private const string ReplaceProfileFieldToolName = "replace_profile_field";

    private sealed record CharacterProfileUpdateToolOperationResult(
        ReplaceProfileFieldResult Result,
        CharacterProfileUpdateRejectedToolCall? RejectedToolCall);

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex Markdown = new(@"(^|\s)(#{1,6}\s|[-*+]\s|>\s|```)|(\[[^\]]+\]\([^)]+\))|(\*\*|__|`)", RegexOptions.Compiled);

    private readonly int characterId;
    private readonly string characterName;
    private readonly CharacterProfileUpdateContext context;
    private readonly IReadOnlyList<string> evidencePointers;
    private readonly CharacterDossierEditSession store;
    private readonly CharacterProfileUpdateStatistics statistics;

    internal CharacterProfileUpdateToolAdapter(
        int characterId,
        string characterName,
        CharacterProfileUpdateContext context,
        IReadOnlyList<string> evidencePointers,
        CharacterDossierEditSession store,
        CharacterProfileUpdateStatistics statistics)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(characterName);
        this.characterId = characterId;
        this.characterName = characterName;
        this.context = context ?? throw new ArgumentNullException(nameof(context));
        this.evidencePointers = evidencePointers ?? throw new ArgumentNullException(nameof(evidencePointers));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.statistics = statistics ?? throw new ArgumentNullException(nameof(statistics));
        this.store.GetRequired(characterId);
    }

    [Description("Replaces one profile field for the current character.")]
    public ReplaceProfileFieldResult ReplaceProfileField(
        [Description("Profile field to replace: Appearance, StatusAndCompetence, PsychologicalProfile, or SpeechAndCommunication.")] CharacterBibleProfileField field,
        [Description("Complete new value of the profile field. This is a replacement, not text to append. Do not use Markdown.")] string value)
    {
        statistics.ToolCalls++;

        var operationResult = ValidateAndApply(field, value);
        var result = operationResult.Result;
        Increment(field, result.Status);
        LogRejectedToolCall(operationResult.RejectedToolCall);
        CharacterBibleRunLogScope.Current?.Info(
            "profile.update.tool.call",
            $"characterId={characterId} name={LogValueFormatter.Quote(characterName)} field={field} valueLength={value?.Length ?? 0}");
        CharacterBibleRunLogScope.Current?.Debug(
            "profile.update.tool.value",
            $"characterId={characterId} name={LogValueFormatter.Quote(characterName)} field={field} value={LogValueFormatter.Quote(value)}");
        return result;
    }

    private CharacterProfileUpdateToolOperationResult ValidateAndApply(
        CharacterBibleProfileField field,
        string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Rejected(field, value, "empty_value", "Value is empty.");
        }

        if (!Enum.IsDefined(field))
        {
            return Rejected(field, value, "unknown_profile_field", "Profile field is unsupported.");
        }

        var normalizedValue = NormalizeWhitespace(value);
        if (normalizedValue.Length > MaxProfileFieldLength)
        {
            return Rejected(field, normalizedValue, "value_too_long", "Value is too long.");
        }

        if (Markdown.IsMatch(normalizedValue))
        {
            return Rejected(field, normalizedValue, "contains_prompt_artifact", "Markdown is not allowed.");
        }

        var currentValue = GetField(context.CurrentProfile, field);
        if (string.Equals(NormalizeWhitespace(currentValue), normalizedValue, StringComparison.Ordinal))
        {
            return Completed(new ReplaceProfileFieldResult
            {
                Status = ReplaceProfileFieldResultStatus.NoOp,
                CurrentValue = currentValue
            });
        }

        context.CurrentProfile = SetField(context.CurrentProfile, field, normalizedValue);
        store.UpdateProfile(characterId, context.CurrentProfile);
        return Completed(new ReplaceProfileFieldResult
        {
            Status = ReplaceProfileFieldResultStatus.Applied,
            CurrentValue = normalizedValue
        });
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

    private CharacterProfileUpdateToolOperationResult Rejected(
        CharacterBibleProfileField field,
        string? value,
        string ruleCode,
        string message)
    {
        var rejected = new CharacterProfileUpdateRejectedToolCall(
            ReplaceProfileFieldToolName,
            field.ToString(),
            ruleCode,
            message,
            LogValueFormatter.ShortText(value),
            evidencePointers);
        return new CharacterProfileUpdateToolOperationResult(
            new ReplaceProfileFieldResult
            {
                Status = ReplaceProfileFieldResultStatus.Rejected,
                Message = message
            },
            rejected);
    }

    private static CharacterProfileUpdateToolOperationResult Completed(ReplaceProfileFieldResult result)
        => new(result, null);

    private void Increment(CharacterBibleProfileField field, ReplaceProfileFieldResultStatus status)
    {
        switch (status)
        {
            case ReplaceProfileFieldResultStatus.Applied:
                statistics.Applied++;
                statistics.AppliedFields.Add(field);
                break;
            case ReplaceProfileFieldResultStatus.NoOp:
                statistics.NoOp++;
                break;
            case ReplaceProfileFieldResultStatus.Rejected:
                statistics.Rejected++;
                break;
        }
    }

    private void LogRejectedToolCall(CharacterProfileUpdateRejectedToolCall? rejected)
    {
        if (rejected is null)
        {
            return;
        }

        statistics.RejectedToolCalls.Add(rejected);
        CharacterBibleRunLogScope.Current?.Warning(
            "profile.update.tool.rejected",
            $"characterId={characterId} name={LogValueFormatter.Quote(characterName)} tool={LogValueFormatter.Quote(rejected.ToolName)} field={LogValueFormatter.Quote(rejected.Field)} rule={LogValueFormatter.Quote(rejected.RuleCode)} message={LogValueFormatter.Quote(rejected.Message)} valuePreview={LogValueFormatter.Quote(rejected.RejectedValuePreview)} evidencePointers={LogValueFormatter.List(rejected.EvidencePointers)}");
    }
}
