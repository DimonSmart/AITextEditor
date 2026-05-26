using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiTextEditor.Core.Model;

namespace AiTextEditor.Agent.CharacterBible.Patching;

public sealed class DossierPatchPromptBuilder
{
    private const string SystemPromptResourceName = "AiTextEditor.Agent.CharacterBible.Patching.Prompts.dossier-patch-proposal.system.md";

    private static readonly Lazy<string> SystemPrompt = new(LoadSystemPrompt);

    private static readonly JsonSerializerOptions UserPromptJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    public string BuildSystemPrompt() => SystemPrompt.Value;

    internal string BuildUserPrompt(
        IReadOnlyList<CharacterBibleDossierPatchCandidate> candidates,
        CharacterBibleResolverDecision decision,
        CharacterDossier dossier)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(dossier);

        var payload = new
        {
            task = "propose_dossier_patch",
            candidates = candidates.Select(ToCandidatePayload),
            identityDecision = new
            {
                kind = decision.Kind.ToString(),
                targetEntryId = decision.CharacterId,
                candidateIds = candidates
                    .Select(candidate => candidate.Candidate.CandidateId)
                    .Where(candidateId => !string.IsNullOrWhiteSpace(candidateId))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
                reason = decision.Reason
            },
            dossier = new
            {
                characterId = dossier.CharacterId,
                name = dossier.Name,
                aliases = dossier.Aliases,
                gender = dossier.Gender,
                profile = CharacterProfile.Normalize(dossier.Profile)
            }
        };

        return JsonSerializer.Serialize(payload, UserPromptJsonOptions);
    }

    private static object ToCandidatePayload(CharacterBibleDossierPatchCandidate patchCandidate)
    {
        var candidate = patchCandidate.Candidate;
        return new
        {
            candidateId = candidate.CandidateId,
            canonicalName = candidate.CanonicalName,
            gender = candidate.Gender,
            aliases = candidate.AliasExamples.Select(alias => new
            {
                form = alias.Key,
                evidence = ToAliasEvidence(candidate, alias.Key, alias.Value)
            }),
            evidence = candidate.Evidence.Select(evidence => new
            {
                pointer = evidence.Pointer,
                anchorExcerpt = evidence.Excerpt
            }),
            evidenceContexts = patchCandidate.EvidenceContexts.Select(context => new
            {
                pointer = context.Pointer,
                anchorExcerpt = context.AnchorExcerpt,
                currentParagraph = context.CurrentParagraph,
                focusedText = context.FocusedText,
                nearbyParagraphs = context.NearbyParagraphs.Select(paragraph => new
                {
                    pointer = paragraph.Pointer,
                    text = paragraph.Text,
                    position = paragraph.Position
                })
            })
        };
    }

    private static object ToAliasEvidence(
        CharacterBibleCharacterCandidate candidate,
        string alias,
        string excerpt)
    {
        var evidence = candidate.AliasEvidence.TryGetValue(alias, out var aliasEvidence)
            ? aliasEvidence
            : candidate.Evidence.FirstOrDefault();

        return new
        {
            pointer = evidence?.Pointer ?? string.Empty,
            excerpt
        };
    }

    private static string LoadSystemPrompt()
    {
        var assembly = typeof(DossierPatchPromptBuilder).Assembly;
        using var stream = assembly.GetManifestResourceStream(SystemPromptResourceName);
        if (stream is null)
        {
            throw new InvalidOperationException($"Embedded dossier patch prompt resource '{SystemPromptResourceName}' was not found.");
        }

        using var reader = new StreamReader(stream);
        var prompt = reader.ReadToEnd();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new InvalidOperationException($"Embedded dossier patch prompt resource '{SystemPromptResourceName}' is empty.");
        }

        return prompt.Trim();
    }
}
