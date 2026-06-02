using AiTextEditor.Agent.CharacterBible.Extraction;
using AiTextEditor.Agent.CharacterBible.Patching;
using AiTextEditor.Agent.CharacterBible.Resolution;
using AiTextEditor.Agent.CharacterBible.VectorSearch;
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
    private readonly CharacterBibleExtractionLimits limits;

    public CharacterDossiersGenerator(
        IDocumentContext documentContext,
        CharacterDossierService dossierService,
        CharacterBibleExtractionLimits limits,
        ILogger<CharacterDossiersGenerator> logger,
        ICharacterExtractionModelClient characterExtractionModelClient,
        CharacterExtractionPromptBuilder promptBuilder,
        ICharacterProfileUpdateModelClient characterProfileUpdateModelClient,
        CharacterProfileUpdatePromptBuilder characterProfileUpdatePromptBuilder,
        ICharacterIdentityResolutionModelClient identityResolutionModelClient,
        ICharacterVectorSearchTool characterVectorSearchTool,
        ILoggerFactory? loggerFactory = null,
        CharacterIdentityResolutionPromptBuilder? identityResolutionPromptBuilder = null,
        ISplitCandidateModelClient? splitCandidateModelClient = null,
        SplitCandidatePromptBuilder? splitCandidatePromptBuilder = null)
    {
        ArgumentNullException.ThrowIfNull(documentContext);
        ArgumentNullException.ThrowIfNull(dossierService);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(characterExtractionModelClient);
        ArgumentNullException.ThrowIfNull(promptBuilder);
        ArgumentNullException.ThrowIfNull(characterProfileUpdateModelClient);
        ArgumentNullException.ThrowIfNull(characterProfileUpdatePromptBuilder);
        ArgumentNullException.ThrowIfNull(identityResolutionModelClient);
        ArgumentNullException.ThrowIfNull(characterVectorSearchTool);

        this.dossierService = dossierService;
        this.limits = limits;
        var paragraphBatcher = new CharacterBibleParagraphBatcher(limits);
        textCollector = new CharacterBibleTextCollector(documentContext, limits, logger);
        candidateExtractor = new CharacterBibleCandidateExtractor(
            characterExtractionModelClient,
            promptBuilder,
            paragraphBatcher,
            loggerFactory?.CreateLogger<CharacterBibleCandidateExtractor>() ?? NullLogger<CharacterBibleCandidateExtractor>.Instance);
        resolver = new CharacterBibleResolver(
            limits,
            identityResolutionModelClient,
            characterVectorSearchTool,
            identityResolutionPromptBuilder,
            splitCandidateModelClient,
            splitCandidatePromptBuilder,
            loggerFactory?.CreateLogger<CharacterBibleResolver>() ?? NullLogger<CharacterBibleResolver>.Instance);
        dossierPatcher = new CharacterBibleDossierPatcher(
            characterProfileUpdateModelClient,
            characterProfileUpdatePromptBuilder,
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

    internal int DocumentItemCount => textCollector.DocumentItemCount;

    internal CharacterBibleExtractionLimits Limits => limits;

    internal Task<CharacterBibleCandidateExtractionResult> ExtractCandidatesAsync(
        IReadOnlyList<TextFragment> paragraphs,
        IProgress<CharacterBibleWorkflowProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return candidateExtractor.ExtractCandidatesAsync(paragraphs, progress, cancellationToken);
    }

    internal CharacterDossierEditSession CreateEditSession()
    {
        return CharacterDossierEditSession.CreateFrom(dossierService.GetDossiers());
    }

    internal Task<CharacterBibleRunState> ResolveCandidatesIntoCatalogAsync(
        CharacterBibleWorkflowInput request,
        CharacterDossierEditSession session,
        int paragraphCount,
        IReadOnlyList<CharacterBibleCharacterCandidate> candidates,
        IProgress<CharacterBibleWorkflowProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return resolver.ResolveAndUpdateCatalogAsync(request, session, paragraphCount, candidates, progress, cancellationToken);
    }

    internal Task<CharacterBibleDossierPatchResult> ApplyDossierPatchesAsync(
        CharacterBibleRunState runState,
        IProgress<CharacterBibleWorkflowProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return dossierPatcher.ApplyDossierPatchesAsync(runState, progress, cancellationToken);
    }

    internal CharacterDossiers FinishRun(CharacterBibleRunState runState)
    {
        ArgumentNullException.ThrowIfNull(runState);

        return runState.Catalog.Current;
    }

    internal CharacterDossiers GetCurrentDossiers() => dossierService.GetDossiers();

    public async Task<CharacterDossiers> GenerateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var paragraphs = CollectParagraphs(null);
        var extraction = await ExtractCandidatesAsync(paragraphs, cancellationToken: cancellationToken);
        var session = CreateEditSession();
        var runState = await ResolveCandidatesIntoCatalogAsync(new CharacterBibleWorkflowInput(), session, paragraphs.Count, extraction.Candidates, cancellationToken: cancellationToken);
        runState = (await ApplyDossierPatchesAsync(runState, cancellationToken: cancellationToken)).RunState;
        return FinishRun(runState);
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
        var session = CreateEditSession();
        var runState = await ResolveCandidatesIntoCatalogAsync(new CharacterBibleWorkflowInput(changedPointers), session, paragraphs.Count, extraction.Candidates, cancellationToken: cancellationToken);
        runState = (await ApplyDossierPatchesAsync(runState, cancellationToken: cancellationToken)).RunState;
        return FinishRun(runState);
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
        var session = CreateEditSession();
        var runState = await ResolveCandidatesIntoCatalogAsync(new CharacterBibleWorkflowInput(changedPointers), session, paragraphs.Count, extraction.Candidates, cancellationToken: cancellationToken);
        runState = (await ApplyDossierPatchesAsync(runState, cancellationToken: cancellationToken)).RunState;
        return FinishRun(runState);
    }
}
