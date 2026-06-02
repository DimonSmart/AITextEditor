using System.Text;
using AiTextEditor.Agent.CharacterBible.Diagnostics;
using AiTextEditor.Core.Model;
using Microsoft.Extensions.Logging;

namespace AiTextEditor.Agent.CharacterBible.Patching;

internal sealed record CharacterBibleProfileUpdateResult(
    CharacterBibleRunState RunState,
    CharacterProfileUpdateStatistics Statistics);

internal sealed class CharacterBibleProfileUpdater
{
    private readonly ICharacterProfileUpdateModelClient modelClient;
    private readonly CharacterProfileUpdatePromptBuilder promptBuilder;
    private readonly CharacterBibleEvidenceContextExpander evidenceContextExpander;
    private readonly CharacterBibleDossierPatchLimits patchLimits;
    private readonly ILogger<CharacterBibleProfileUpdater> logger;

    public CharacterBibleProfileUpdater(
        ICharacterProfileUpdateModelClient modelClient,
        CharacterProfileUpdatePromptBuilder promptBuilder,
        CharacterBibleEvidenceContextExpander evidenceContextExpander,
        CharacterBibleDossierPatchLimits? patchLimits,
        ILogger<CharacterBibleProfileUpdater> logger)
    {
        this.modelClient = modelClient ?? throw new ArgumentNullException(nameof(modelClient));
        this.promptBuilder = promptBuilder ?? throw new ArgumentNullException(nameof(promptBuilder));
        this.evidenceContextExpander = evidenceContextExpander ?? throw new ArgumentNullException(nameof(evidenceContextExpander));
        this.patchLimits = patchLimits ?? new CharacterBibleDossierPatchLimits();
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<CharacterBibleProfileUpdateResult> UpdateProfilesAsync(
        CharacterBibleRunState runState,
        IProgress<CharacterBibleWorkflowProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runState);
        cancellationToken.ThrowIfCancellationRequested();

        var statistics = new CharacterProfileUpdateStatistics();
        var processedCharacterIds = new HashSet<int>();
        if (runState.Failure is not null || runState.Candidates.Count == 0 || runState.Catalog.Decisions.Count == 0)
        {
            return new CharacterBibleProfileUpdateResult(runState, statistics);
        }

        foreach (var patchGroup in BuildPatchGroups(runState))
        {
            CharacterDossier dossier;
            try
            {
                dossier = runState.Catalog.GetRequired(patchGroup.CharacterId);
            }
            catch (InvalidOperationException)
            {
                CharacterBibleRunLogScope.Current?.Warning(
                    "profile.update.group.skipped",
                    $"characterId={patchGroup.CharacterId} reason={LogValueFormatter.Quote("character not found")}");
                continue;
            }

            var evidence = CharacterProfileUpdatePromptBuilder.BuildEvidence(patchGroup.Candidates);
            if (evidence.Count == 0)
            {
                continue;
            }

            if (processedCharacterIds.Add(dossier.CharacterId))
            {
                statistics.CharactersProcessed++;
            }
            statistics.AgentCalls++;
            CharacterBibleRunLogScope.Current?.Info(
                "profile.update.group.start",
                $"characterId={dossier.CharacterId} name={LogValueFormatter.Quote(dossier.Name)} candidateCount={patchGroup.Candidates.Count} evidencePointers={LogValueFormatter.List(evidence.Select(item => item.Pointer))}");
            progress?.Report(new CharacterBibleWorkflowProgress(
                "patch",
                $"Updating profile for {dossier.Name} from {evidence.Count} evidence paragraph(s)."));

            var context = new CharacterProfileUpdateContext(
                dossier.Profile,
                evidence.Select(item => item.Pointer).ToHashSet(StringComparer.Ordinal),
                evidence.ToDictionary(item => item.Pointer, item => item.Text, StringComparer.Ordinal));
            var tool = new CharacterProfileUpdateToolAdapter(
                dossier.CharacterId,
                dossier.Name,
                context,
                runState.Catalog,
                statistics);
            var promptInput = CharacterProfileUpdatePromptBuilder.BuildPromptInput(patchGroup.Candidates, dossier);

            try
            {
                CharacterBibleLlmInputLogger.DebugInput(
                    "profile.update.llm.input",
                    $"characterId={dossier.CharacterId} modelKeys={LogValueFormatter.List(["target", "currentProfile", "newEvidence"])} modelType={nameof(CharacterProfileUpdatePromptInput)}",
                    promptInput);
                var appliedBefore = statistics.Applied;
                await modelClient.UpdateProfileAsync(
                    new CharacterProfileUpdateModelRequest(
                        promptBuilder.BuildSystemPrompt(),
                        promptBuilder.BuildUserPrompt(promptInput),
                        tool,
                        new CharacterBibleAgentDiagnosticProgress(
                            progress,
                            "patch",
                            $"Profile update for {dossier.Name}",
                            $"characterId={dossier.CharacterId} name={LogValueFormatter.Quote(dossier.Name)}")),
                    cancellationToken).ConfigureAwait(false);
                CharacterBibleRunLogScope.Current?.Info(
                    statistics.Applied == appliedBefore ? "profile.update.no_change" : "profile.update.applied",
                    $"characterId={dossier.CharacterId} name={LogValueFormatter.Quote(dossier.Name)} applied={statistics.Applied - appliedBefore}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                CharacterBibleRunLogScope.Current?.Error(
                    "profile.update.failed",
                    $"characterId={dossier.CharacterId} name={LogValueFormatter.Quote(dossier.Name)} message={LogValueFormatter.Quote(ex.Message)}",
                    ex);
                logger.LogError(ex, "Character profile update failed for character {CharacterId}. Applied tool updates were preserved.", dossier.CharacterId);
                progress?.Report(new CharacterBibleWorkflowProgress(
                    "patch",
                    $"Profile update failed for {dossier.Name}; valid tool updates already applied were preserved.",
                    IsError: true));
            }
        }

        progress?.Report(new CharacterBibleWorkflowProgress(
            "patch",
            $"Character profile updating finished: {statistics.ProfileFieldsChanged} field update(s) applied."));
        return new CharacterBibleProfileUpdateResult(runState, statistics);
    }

    private IReadOnlyList<DossierPatchGroup> BuildPatchGroups(CharacterBibleRunState runState)
    {
        var groups = new List<DossierPatchGroupBuilder>();
        var groupsByCharacterId = new Dictionary<int, DossierPatchGroupBuilder>();

        for (var index = 0; index < runState.Catalog.Decisions.Count && index < runState.Candidates.Count; index++)
        {
            var decision = runState.Catalog.Decisions[index];
            if (decision.Kind is not CharacterBibleDecisionKind.Existing and not CharacterBibleDecisionKind.New
                || decision.CharacterId is null)
            {
                continue;
            }

            var characterId = decision.CharacterId.Value;
            if (!groupsByCharacterId.TryGetValue(characterId, out var group))
            {
                group = new DossierPatchGroupBuilder(characterId);
                groupsByCharacterId[characterId] = group;
                groups.Add(group);
            }

            var candidate = runState.Candidates[index];
            group.Candidates.Add(new CharacterBibleDossierPatchCandidate(
                candidate,
                evidenceContextExpander.Expand(candidate.Evidence)));
        }

        return groups
            .Where(group => group.Candidates.Count > 0)
            .SelectMany(SplitPatchGroup)
            .ToArray();
    }

    private IEnumerable<DossierPatchGroup> SplitPatchGroup(DossierPatchGroupBuilder group)
    {
        var maxCandidates = Math.Max(1, patchLimits.MaxCandidatesPerPatchCall);
        var maxBytes = Math.Max(1, patchLimits.MaxContextBytesPerPatchCall);
        var batch = new List<CharacterBibleDossierPatchCandidate>();
        var batchBytes = 0;

        foreach (var candidate in group.Candidates)
        {
            var candidateBytes = EstimatePatchCandidateBytes(candidate);
            if ((batch.Count >= maxCandidates || batchBytes + candidateBytes > maxBytes) && batch.Count > 0)
            {
                yield return new DossierPatchGroup(group.CharacterId, batch.ToArray());
                batch = [];
                batchBytes = 0;
            }

            batch.Add(candidate);
            batchBytes += candidateBytes;
        }

        if (batch.Count > 0)
        {
            yield return new DossierPatchGroup(group.CharacterId, batch.ToArray());
        }
    }

    private static int EstimatePatchCandidateBytes(CharacterBibleDossierPatchCandidate patchCandidate)
    {
        var bytes = 0;
        foreach (var context in patchCandidate.EvidenceContexts)
        {
            bytes += GetUtf8ByteCount(context.Pointer)
                     + GetUtf8ByteCount(context.AnchorExcerpt)
                     + GetUtf8ByteCount(context.CurrentParagraph)
                     + GetUtf8ByteCount(context.FocusedText);

            foreach (var nearby in context.NearbyParagraphs)
            {
                bytes += GetUtf8ByteCount(nearby.Pointer)
                         + GetUtf8ByteCount(nearby.Text)
                         + GetUtf8ByteCount(nearby.Position);
            }
        }

        return bytes;
    }

    private static int GetUtf8ByteCount(string? value)
        => string.IsNullOrEmpty(value) ? 0 : Encoding.UTF8.GetByteCount(value);

    private sealed record DossierPatchGroup(
        int CharacterId,
        IReadOnlyList<CharacterBibleDossierPatchCandidate> Candidates);

    private sealed class DossierPatchGroupBuilder(int characterId)
    {
        public int CharacterId { get; } = characterId;

        public List<CharacterBibleDossierPatchCandidate> Candidates { get; } = [];
    }
}
