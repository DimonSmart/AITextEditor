using AiTextEditor.Agent.CharacterBible.Extraction;
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

    public CharacterDossiersGenerator(
        IDocumentContext documentContext,
        CharacterDossierService dossierService,
        CharacterBibleExtractionLimits limits,
        ILogger<CharacterDossiersGenerator> logger,
        ICharacterExtractionModelClient characterExtractionModelClient,
        CharacterExtractionPromptBuilder promptBuilder,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(documentContext);
        ArgumentNullException.ThrowIfNull(dossierService);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(characterExtractionModelClient);
        ArgumentNullException.ThrowIfNull(promptBuilder);

        this.dossierService = dossierService;
        var paragraphBatcher = new CharacterBibleParagraphBatcher(limits);
        textCollector = new CharacterBibleTextCollector(documentContext, limits, logger);
        candidateExtractor = new CharacterBibleCandidateExtractor(
            characterExtractionModelClient,
            promptBuilder,
            paragraphBatcher,
            loggerFactory?.CreateLogger<CharacterBibleCandidateExtractor>() ?? NullLogger<CharacterBibleCandidateExtractor>.Instance);
        resolver = new CharacterBibleResolver(dossierService, limits);
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

    internal CharacterBibleCommitPlan CreateCommitPlan(
        CharacterBibleWorkflowInput request,
        int paragraphCount,
        IReadOnlyList<CharacterBibleCharacterCandidate> candidates,
        IProgress<CharacterBibleWorkflowProgress>? progress = null)
    {
        return resolver.CreateCommitPlan(request, paragraphCount, candidates, progress);
    }

    internal CharacterDossiers CommitPlan(CharacterBibleCommitPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (plan.Changed)
        {
            dossierService.ReplaceDossiers(plan.ProjectedDossiers.Characters);
        }

        return dossierService.GetDossiers();
    }

    internal CharacterDossiers GetCurrentDossiers() => dossierService.GetDossiers();

    public async Task<CharacterDossiers> GenerateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var paragraphs = CollectParagraphs(null);
        var extraction = await ExtractCandidatesAsync(paragraphs, cancellationToken: cancellationToken);
        var plan = CreateCommitPlan(new CharacterBibleWorkflowInput(), paragraphs.Count, extraction.Candidates);
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
        var plan = CreateCommitPlan(new CharacterBibleWorkflowInput(changedPointers), paragraphs.Count, extraction.Candidates);
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
        var plan = CreateCommitPlan(new CharacterBibleWorkflowInput(changedPointers), paragraphs.Count, extraction.Candidates);
        return CommitPlan(plan);
    }
}
