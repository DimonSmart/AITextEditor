using AiTextEditor.Agent.CharacterBible.Extraction;
using AiTextEditor.Agent.CharacterBible.Commit;
using AiTextEditor.Agent.CharacterBible.Patching;
using AiTextEditor.Agent.CharacterBible.Resolution;
using AiTextEditor.Core.Model;
using AiTextEditor.Core.Services;
using AiTextEditor.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiTextEditor.Agent.CharacterBible;

public sealed class CharacterDossiersGenerator
{
    private readonly CharacterDossierService dossierService;
    private readonly CharacterBibleTextCollector textCollector;
    private readonly CharacterBibleCandidateExtractor candidateExtractor;
    private readonly CharacterBibleResolver resolver;
    private readonly CharacterBibleDossierPatcher dossierPatcher;
    private readonly CharacterBibleCommitter committer;

    public CharacterDossiersGenerator(
        IDocumentContext documentContext,
        CharacterDossierService dossierService,
        CharacterBibleExtractionLimits limits,
        ILogger<CharacterDossiersGenerator> logger,
        ICharacterExtractionModelClient characterExtractionModelClient,
        CharacterExtractionPromptBuilder promptBuilder,
        IDossierPatchProposalModelClient dossierPatchProposalModelClient,
        DossierPatchPromptBuilder dossierPatchPromptBuilder,
        IDossierConsistencyReviewerModelClient dossierConsistencyReviewerModelClient,
        DossierConsistencyReviewerPromptBuilder dossierConsistencyReviewerPromptBuilder,
        ILoggerFactory? loggerFactory = null,
        ISuspectArchiveResolverModelClient? suspectArchiveResolverModelClient = null,
        SuspectArchiveResolverPromptBuilder? suspectArchiveResolverPromptBuilder = null,
        ISplitCandidateModelClient? splitCandidateModelClient = null,
        SplitCandidatePromptBuilder? splitCandidatePromptBuilder = null)
    {
        ArgumentNullException.ThrowIfNull(documentContext);
        ArgumentNullException.ThrowIfNull(dossierService);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(characterExtractionModelClient);
        ArgumentNullException.ThrowIfNull(promptBuilder);
        ArgumentNullException.ThrowIfNull(dossierPatchProposalModelClient);
        ArgumentNullException.ThrowIfNull(dossierPatchPromptBuilder);
        ArgumentNullException.ThrowIfNull(dossierConsistencyReviewerModelClient);
        ArgumentNullException.ThrowIfNull(dossierConsistencyReviewerPromptBuilder);

        this.dossierService = dossierService;
        var paragraphBatcher = new CharacterBibleParagraphBatcher(limits);
        textCollector = new CharacterBibleTextCollector(documentContext, limits, logger);
        candidateExtractor = new CharacterBibleCandidateExtractor(
            characterExtractionModelClient,
            promptBuilder,
            paragraphBatcher,
            loggerFactory?.CreateLogger<CharacterBibleCandidateExtractor>() ?? NullLogger<CharacterBibleCandidateExtractor>.Instance);
        resolver = new CharacterBibleResolver(
            dossierService,
            limits,
            suspectArchiveResolverModelClient,
            suspectArchiveResolverPromptBuilder,
            splitCandidateModelClient,
            splitCandidatePromptBuilder,
            loggerFactory?.CreateLogger<CharacterBibleResolver>() ?? NullLogger<CharacterBibleResolver>.Instance);
        committer = new CharacterBibleCommitter(dossierService);
        dossierPatcher = new CharacterBibleDossierPatcher(
            dossierPatchProposalModelClient,
            dossierPatchPromptBuilder,
            dossierConsistencyReviewerModelClient,
            dossierConsistencyReviewerPromptBuilder,
            new CharacterBibleEvidenceContextExpander(documentContext),
            null,
            loggerFactory?.CreateLogger<CharacterBibleDossierPatcher>() ?? NullLogger<CharacterBibleDossierPatcher>.Instance);
    }

    internal IReadOnlyList<TextFragment> CollectParagraphs(
        IReadOnlyCollection<string>? changedPointers,
        IProgress<CharacterBibleWorkflowProgress>? progress = null)
    {
        return textCollector.CollectParagraphs(changedPointers, progress);
    }

    internal Task<CharacterBibleCandidateExtractionResult> ExtractCandidatesAsync(
        IReadOnlyList<TextFragment> paragraphs,
        IProgress<CharacterBibleWorkflowProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return candidateExtractor.ExtractCandidatesAsync(paragraphs, progress, cancellationToken);
    }

    internal Task<CharacterBibleCommitPlan> CreateCommitPlanAsync(
        CharacterBibleWorkflowInput request,
        int paragraphCount,
        IReadOnlyList<CharacterBibleCharacterCandidate> candidates,
        IProgress<CharacterBibleWorkflowProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return resolver.CreateCommitPlanAsync(request, paragraphCount, candidates, progress, cancellationToken);
    }

    internal Task<CharacterBibleCommitPlan> ApplyDossierPatchesAsync(
        CharacterBibleCommitPlan plan,
        IProgress<CharacterBibleWorkflowProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return dossierPatcher.ApplyDossierPatchesAsync(plan, progress, cancellationToken);
    }

    internal CharacterDossiers CommitPlan(CharacterBibleCommitPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        return committer.Commit(plan);
    }

    internal CharacterDossiers GetCurrentDossiers() => dossierService.GetDossiers();

    public async Task<CharacterDossiers> GenerateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var paragraphs = CollectParagraphs(null);
        var extraction = await ExtractCandidatesAsync(paragraphs, cancellationToken: cancellationToken);
        var plan = await CreateCommitPlanAsync(new CharacterBibleWorkflowInput(), paragraphs.Count, extraction.Candidates, cancellationToken: cancellationToken);
        plan = await ApplyDossierPatchesAsync(plan, cancellationToken: cancellationToken);
        return CommitPlan(plan);
    }

    public async Task<CharacterDossiers> RefreshAsync(
        IReadOnlyCollection<string>? changedPointers,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (changedPointers is null || changedPointers.Count == 0)
        {
            return await GenerateAsync(cancellationToken);
        }

        var paragraphs = CollectParagraphs(changedPointers);
        if (paragraphs.Count == 0)
        {
            return dossierService.GetDossiers();
        }

        var extraction = await ExtractCandidatesAsync(paragraphs, cancellationToken: cancellationToken);
        var plan = await CreateCommitPlanAsync(new CharacterBibleWorkflowInput(changedPointers), paragraphs.Count, extraction.Candidates, cancellationToken: cancellationToken);
        plan = await ApplyDossierPatchesAsync(plan, cancellationToken: cancellationToken);
        return CommitPlan(plan);
    }

    public async Task<CharacterDossiers> UpdateFromEvidenceBatchAsync(
        IReadOnlyList<EvidenceItem> evidence,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        cancellationToken.ThrowIfCancellationRequested();

        var paragraphs = CharacterBibleTextCollector.CollectParagraphsFromEvidence(evidence);
        if (paragraphs.Count == 0)
        {
            return dossierService.GetDossiers();
        }

        var textFragments = paragraphs
            .Select(paragraph => new TextFragment(paragraph.Pointer, paragraph.Text))
            .ToArray();
        var extraction = await ExtractCandidatesAsync(textFragments, cancellationToken: cancellationToken);
        var changedPointers = paragraphs.Select(paragraph => paragraph.Pointer).ToArray();
        var plan = await CreateCommitPlanAsync(new CharacterBibleWorkflowInput(changedPointers), paragraphs.Count, extraction.Candidates, cancellationToken: cancellationToken);
        plan = await ApplyDossierPatchesAsync(plan, cancellationToken: cancellationToken);
        return CommitPlan(plan);
    }
}
