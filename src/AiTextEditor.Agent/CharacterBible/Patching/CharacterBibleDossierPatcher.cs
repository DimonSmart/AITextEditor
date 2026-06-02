using System.Text;
using AiTextEditor.Agent.CharacterBible.Diagnostics;
using AiTextEditor.Core.Model;
using Microsoft.Extensions.Logging;

namespace AiTextEditor.Agent.CharacterBible.Patching;

internal sealed record CharacterBibleDossierPatchResult(
    CharacterBibleRunState RunState,
    CharacterProfilePatchStatistics Statistics);

internal sealed class CharacterBibleDossierPatcher
{
    private readonly ICharacterProfilePatchModelClient modelClient;
    private readonly DossierPatchPromptBuilder promptBuilder;
    private readonly CharacterBibleEvidenceContextExpander evidenceContextExpander;
    private readonly CharacterBibleDossierPatchLimits patchLimits;
    private readonly ILogger<CharacterBibleDossierPatcher> logger;

    public CharacterBibleDossierPatcher(
        ICharacterProfilePatchModelClient modelClient,
        DossierPatchPromptBuilder promptBuilder,
        CharacterBibleEvidenceContextExpander evidenceContextExpander,
        CharacterBibleDossierPatchLimits? patchLimits,
        ILogger<CharacterBibleDossierPatcher> logger)
    {
        this.modelClient = modelClient ?? throw new ArgumentNullException(nameof(modelClient));
        this.promptBuilder = promptBuilder ?? throw new ArgumentNullException(nameof(promptBuilder));
        this.evidenceContextExpander = evidenceContextExpander ?? throw new ArgumentNullException(nameof(evidenceContextExpander));
        this.patchLimits = patchLimits ?? new CharacterBibleDossierPatchLimits();
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<CharacterBibleDossierPatchResult> ApplyDossierPatchesAsync(
        CharacterBibleRunState runState,
        IProgress<CharacterBibleWorkflowProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runState);
        cancellationToken.ThrowIfCancellationRequested();

        var statistics = new CharacterProfilePatchStatistics();
        var processedCharacterIds = new HashSet<string>(StringComparer.Ordinal);
        if (runState.Failure is not null || runState.Candidates.Count == 0 || runState.Catalog.Decisions.Count == 0)
        {
            return new CharacterBibleDossierPatchResult(runState, statistics);
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
                    "patch.group.skipped",
                    $"characterId={patchGroup.CharacterId} reason={LogValueFormatter.Quote("character not found")}");
                continue;
            }

            var evidence = DossierPatchPromptBuilder.BuildEvidence(patchGroup.Candidates);
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
                "patch.group.start",
                $"characterId={dossier.CharacterId} name={LogValueFormatter.Quote(dossier.Name)} candidateCount={patchGroup.Candidates.Count} evidencePointers={LogValueFormatter.List(evidence.Select(item => item.Pointer))}");
            progress?.Report(new CharacterBibleWorkflowProgress(
                "patch",
                $"Updating profile for {dossier.Name} from {evidence.Count} evidence paragraph(s)."));

            var context = new CharacterProfilePatchContext(
                dossier.Profile,
                evidence.Select(item => item.Pointer).ToHashSet(StringComparer.Ordinal),
                evidence.ToDictionary(item => item.Pointer, item => item.Text, StringComparer.Ordinal));
            var tools = new CharacterProfilePatchTools(
                dossier.CharacterId,
                dossier.Name,
                context,
                runState.Catalog,
                statistics);
            var promptInput = DossierPatchPromptBuilder.BuildPromptInput(patchGroup.Candidates, dossier);

            try
            {
                CharacterBibleLlmInputLogger.DebugInput(
                    "patch.llm.input",
                    $"characterId={dossier.CharacterId} modelKeys={LogValueFormatter.List(["character", "evidence"])} modelType={nameof(CharacterProfilePatchPromptInput)}",
                    promptInput);
                await modelClient.PatchAsync(
                    new CharacterProfilePatchModelRequest(
                        promptBuilder.BuildSystemPrompt(),
                        promptBuilder.BuildUserPrompt(promptInput),
                        tools,
                        new CharacterBibleAgentDiagnosticProgress(
                            progress,
                            "patch",
                            $"Profile patch for {dossier.Name}",
                            $"characterId={dossier.CharacterId} name={LogValueFormatter.Quote(dossier.Name)}")),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                CharacterBibleRunLogScope.Current?.Error(
                    "patch.failed",
                    $"characterId={dossier.CharacterId} name={LogValueFormatter.Quote(dossier.Name)} message={LogValueFormatter.Quote(ex.Message)}",
                    ex);
                logger.LogError(ex, "Dossier profile patch failed for character {CharacterId}. Applied tool updates were preserved.", dossier.CharacterId);
                progress?.Report(new CharacterBibleWorkflowProgress(
                    "patch",
                    $"Profile patch failed for {dossier.Name}; valid tool updates already applied were preserved.",
                    IsError: true));
            }
        }

        progress?.Report(new CharacterBibleWorkflowProgress(
            "patch",
            $"Dossier profile updating finished: {statistics.ProfileFieldsChanged} field update(s) applied."));
        return new CharacterBibleDossierPatchResult(runState, statistics);
    }

    private IReadOnlyList<DossierPatchGroup> BuildPatchGroups(CharacterBibleRunState runState)
    {
        var groups = new List<DossierPatchGroupBuilder>();
        var groupsByCharacterId = new Dictionary<string, DossierPatchGroupBuilder>(StringComparer.Ordinal);

        for (var index = 0; index < runState.Catalog.Decisions.Count && index < runState.Candidates.Count; index++)
        {
            var decision = runState.Catalog.Decisions[index];
            if (decision.Kind is not CharacterBibleDecisionKind.Existing and not CharacterBibleDecisionKind.New
                || string.IsNullOrWhiteSpace(decision.CharacterId))
            {
                continue;
            }

            var characterId = decision.CharacterId.Trim();
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
        string CharacterId,
        IReadOnlyList<CharacterBibleDossierPatchCandidate> Candidates);

    private sealed class DossierPatchGroupBuilder(string characterId)
    {
        public string CharacterId { get; } = characterId;

        public List<CharacterBibleDossierPatchCandidate> Candidates { get; } = [];
    }
}
